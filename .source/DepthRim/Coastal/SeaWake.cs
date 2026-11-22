using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 船體在地圖上的朝向固定、航向任意，所以航跡不靠移動船體，而是在海面著色器（DepthRim_SeaSurface）裡畫：
    /// <list type="bullet">
    /// <item>海水往船尾方向流過（<c>_Flow</c>：累積的位移，格）：漣漪、色塊、浪花雜訊都跟著流，看起來船在前進。暫停時不動。</item>
    /// <item>開爾文波：從船艏往後張開約 19.5° 的兩道 V 形浪，帶細碎的斜向羽狀紋，越遠越淡。</item>
    /// <item>船尾的翻攪水（螺旋槳尾流）：船寬的一條白沫帶，往後慢慢變寬、變淡。</item>
    /// <item>船艏浪：船艏前方與兩側一圈白沫。</item>
    /// </list>
    /// 白沫的雜訊取樣在「跟著海水流」的座標上，所以白沫留在水裡往後流，航跡的形狀固定在船後面。
    /// 航向來自世界地圖上這一格到下一格的方位（<see cref="WorldObject_SurfaceShip"/>）；船體外形是船體平台格投影到航向上的外框。
    /// 這裡是純函式（可單元測試），參數由 <see cref="MapComponent_SeaSurface"/> 每幀設到材質上。
    /// </summary>
    public static class SeaWake
    {
        /// <summary>全速時海水流過的速度（格／遊戲秒；一倍速 = 真實秒）。</summary>
        public const float FlowCellsPerSecond = 3f;

        /// <summary>航跡強度跟上推力變化的速度（每真實秒）。</summary>
        public const float SpeedChangePerSecond = 0.5f;

        /// <summary>流過的位移超過這麼多格就繞回（雜訊不是週期的，所以繞回時會跳一下；夠大就很少發生）。</summary>
        public const float FlowWrap = 20000f;

        /// <summary>
        /// 世界地圖方位（度：0 = 北、90 = 東，原版 <c>WorldGrid.GetHeadingFromTo</c>）換成地圖上的單位向量（x = 東，z = 北）。
        /// </summary>
        public static Vector2 HeadingVector(float degrees)
        {
            float r = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
        }

        /// <summary>
        /// 推進器推的方向（2026-11-21：船在地圖上照推進器的推力方向前進，不看世界地圖上的航線方位）：
        /// 每座推進器往它的朝向推（和 DMSE 引擎一樣，噴口在背面），全部加起來取單位向量（x = 東，z = 北）。
        /// 沒有推進器或互相抵消時 null。
        /// </summary>
        public static Vector2? ThrustHeading(IEnumerable<Rot4> thrusters)
        {
            Vector2 sum = Vector2.zero;
            foreach (Rot4 r in thrusters)
            {
                IntVec3 v = r.FacingCell;
                sum += new Vector2(v.x, v.z);
            }
            return sum.sqrMagnitude > 0.01f ? sum.normalized : (Vector2?)null;
        }

        /// <summary>地圖上的水面推進器（蓋好的）的推力方向，見 <see cref="ThrustHeading(IEnumerable{Rot4})"/>。</summary>
        public static Vector2? ThrustHeading(Map map)
        {
            ThingDef def = DepthRimDefOf.DepthRim_SurfaceThruster;
            if (map == null || def == null)
            {
                return null;
            }
            return ThrustHeading(map.listerThings.ThingsOfDef(def).Where(t => t.Spawned).Select(t => t.Rotation));
        }

        /// <summary>航向的右手邊（地圖上順時針轉 90°）。</summary>
        public static Vector2 Side(Vector2 heading) => new Vector2(heading.y, -heading.x);

        /// <summary>船體在航向上的外框：船艏中點（格）、長度（沿航向）、半寬（垂直航向）。</summary>
        public struct Hull
        {
            public Vector2 bow;
            public float length;
            public float halfWidth;
            public bool valid;

            /// <summary>
            /// 船艏的輪廓（2026-11-21：船艏浪要貼著船艏的形狀，不是整個外框的寬度）：從船艏往後第 k 列（每列一格，<see cref="BowRows"/> 列）
            /// 船體離中線最遠到哪裡（半寬，格）。比外框窄的尖船艏在這裡看得出來。
            /// </summary>
            public float[] bowProfile;

            public Vector4 ToShader() => valid ? new Vector4(bow.x, bow.y, length, halfWidth) : Vector4.zero;

            /// <summary>船艏輪廓前四列（著色器的 _BowA）。</summary>
            public Vector4 BowA => valid && bowProfile != null ? new Vector4(bowProfile[0], bowProfile[1], bowProfile[2], bowProfile[3]) : new Vector4(halfWidth, halfWidth, halfWidth, halfWidth);

            /// <summary>船艏輪廓後四列（著色器的 _BowB）。</summary>
            public Vector4 BowB => valid && bowProfile != null ? new Vector4(bowProfile[4], bowProfile[5], bowProfile[6], bowProfile[7]) : new Vector4(halfWidth, halfWidth, halfWidth, halfWidth);
        }

        /// <summary>船艏輪廓取幾列。</summary>
        public const int BowRows = 8;

        /// <summary>
        /// 把船體格（格子中心）投影到航向與側向，取外框：船艏在最前面、側向置中；長度與寬度各加一格（格子本身的大小）。
        /// </summary>
        public static Hull HullAlong(IEnumerable<IntVec3> cells, Vector2 heading)
        {
            Vector2 side = Side(heading);
            float minA = float.MaxValue, maxA = float.MinValue, minL = float.MaxValue, maxL = float.MinValue;
            bool any = false;
            foreach (IntVec3 c in cells)
            {
                Vector2 p = new Vector2(c.x + 0.5f, c.z + 0.5f);
                float a = Vector2.Dot(p, heading);
                float l = Vector2.Dot(p, side);
                minA = Mathf.Min(minA, a);
                maxA = Mathf.Max(maxA, a);
                minL = Mathf.Min(minL, l);
                maxL = Mathf.Max(maxL, l);
                any = true;
            }
            if (!any)
            {
                return default;
            }
            float midL = (minL + maxL) * 0.5f;
            float half = (maxL - minL + 1f) * 0.5f;
            // 船艏輪廓：每列（離最前面那排格子中心 0、1、2… 格）最寬到哪；往後只會變寬（中間缺格時沿用前一列）
            float[] profile = new float[BowRows];
            foreach (IntVec3 c in cells)
            {
                Vector2 p = new Vector2(c.x + 0.5f, c.z + 0.5f);
                int row = Mathf.FloorToInt(maxA - Vector2.Dot(p, heading) + 0.5f);
                if (row >= 0 && row < BowRows)
                {
                    profile[row] = Mathf.Max(profile[row], Mathf.Abs(Vector2.Dot(p, side) - midL) + 0.5f);
                }
            }
            for (int k = 0; k < BowRows; k++)
            {
                if (k > 0)
                {
                    profile[k] = Mathf.Max(profile[k], profile[k - 1]);
                }
                if (maxA - minA + 1f <= k)
                {
                    profile[k] = Mathf.Max(profile[k], k > 0 ? profile[k - 1] : half);
                }
                profile[k] = Mathf.Min(profile[k] <= 0f ? half : profile[k], half);
            }
            return new Hull
            {
                bow = heading * (maxA + 0.5f) + side * midL,
                length = maxA - minA + 1f,
                halfWidth = half,
                bowProfile = profile,
                valid = true,
            };
        }

        /// <summary>海水這一段時間流過的位移（往船尾，所以取樣座標往船艏方向加）：<paramref name="seconds"/> 遊戲秒 × 流速 × 速度比例。</summary>
        public static Vector2 FlowStep(Vector2 heading, float speed, float seconds)
        {
            if (speed <= 0f || seconds <= 0f)
            {
                return Vector2.zero;
            }
            return heading * (FlowCellsPerSecond * Mathf.Clamp01(speed) * seconds);
        }

        /// <summary>累積位移太大時繞回，保持浮點精度。</summary>
        public static Vector2 Wrap(Vector2 flow)
        {
            if (Mathf.Abs(flow.x) > FlowWrap) { flow.x %= FlowWrap; }
            if (Mathf.Abs(flow.y) > FlowWrap) { flow.y %= FlowWrap; }
            return flow;
        }
    }
}
