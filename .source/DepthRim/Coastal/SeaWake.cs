using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水面航行的航跡（M2，2026-11-20 使用者要求：浮航不用艙外淡出，要專門的航跡）。
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

        /// <summary>航向的右手邊（地圖上順時針轉 90°）。</summary>
        public static Vector2 Side(Vector2 heading) => new Vector2(heading.y, -heading.x);

        /// <summary>船體在航向上的外框：船艏中點（格）、長度（沿航向）、半寬（垂直航向）。</summary>
        public struct Hull
        {
            public Vector2 bow;
            public float length;
            public float halfWidth;
            public bool valid;

            public Vector4 ToShader() => valid ? new Vector4(bow.x, bow.y, length, halfWidth) : Vector4.zero;
        }

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
            return new Hull
            {
                bow = heading * (maxA + 0.5f) + side * midL,
                length = maxA - minA + 1f,
                halfWidth = (maxL - minL + 1f) * 0.5f,
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
