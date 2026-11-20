using System.Collections.Generic;
using UnityEngine;

namespace DepthRim
{
    /// <summary>
    /// 進出港演出（M4，設計稿 §6）的船體姿態：位置（地圖座標，格）與旋轉角（度，順時針；0 = 擷取到的影像原本的朝向，也就是停進泊位後的朝向）。
    /// 純函式，可單元測試。離港用同一條時間軸倒著播（<see cref="Pose"/> 的 progress 換成 1 − progress）。
    /// <para>時間軸（比例；完整版 10 秒、短版 3 秒）：
    /// <list type="bullet">
    /// <item>0–0.5：從進場口沿路線開到迴轉點，船頭朝前進方向（短版只走路線最後一段）。</item>
    /// <item>0.5–0.65：在迴轉點原地轉到最後的朝向（最短的那個方向）。</item>
    /// <item>0.65–0.85：從迴轉點平移到泊位（舷靠時是側推；最後擠過頭 <see cref="BumpCells"/> 格再回彈）。</item>
    /// <item>0.85–1：停好，輕輕起伏。</item>
    /// </list></para>
    /// </summary>
    public static class HarborShowMath
    {
        public const float TravelEnd = 0.5f;
        public const float TurnEnd = 0.65f;
        public const float SlideEnd = 0.85f;

        /// <summary>舷靠時碰墊擠壓的幅度（格）。</summary>
        public const float BumpCells = 0.15f;

        /// <summary>短版只走路線的最後這一段（比例）。</summary>
        public const float ShortTravelShare = 0.25f;

        public struct Pose
        {
            public Vector2 pos;
            public float angle;
        }

        /// <summary>地圖方向的羅盤角（度，0 = 北、90 = 東，順時針）。</summary>
        public static float Compass(Vector2 dir)
        {
            return Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
        }

        /// <summary>把角度差收進 −180..180（轉最短的那邊）。</summary>
        public static float Wrap(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) { degrees -= 360f; }
            if (degrees < -180f) { degrees += 360f; }
            return degrees;
        }

        /// <summary>
        /// 停好後船頭朝哪（羅盤角）：船體外框的長邊方向，取和「迴轉點 → 泊位」同向的那一頭（船往前開進泊位）；
        /// 方形的船看哪個軸的位移大。
        /// </summary>
        public static float FinalHeading(int width, int height, Vector2 turn, Vector2 berth)
        {
            Vector2 d = berth - turn;
            bool northSouth = height > width || (height == width && Mathf.Abs(d.y) >= Mathf.Abs(d.x));
            if (northSouth)
            {
                return d.y >= 0f ? 0f : 180f;
            }
            return d.x >= 0f ? 90f : 270f;
        }

        /// <summary>沿折線走到弧長比例 <paramref name="t"/> 的位置與該段的方向。</summary>
        public static Vector2 AlongPath(List<Vector2> path, float t, out Vector2 dir)
        {
            dir = Vector2.up;
            if (path.Count == 0)
            {
                return Vector2.zero;
            }
            if (path.Count == 1)
            {
                return path[0];
            }
            float total = 0f;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                total += (path[i + 1] - path[i]).magnitude;
            }
            float target = Mathf.Clamp01(t) * total;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                Vector2 seg = path[i + 1] - path[i];
                float len = seg.magnitude;
                if (len <= 1e-5f)
                {
                    continue;
                }
                dir = seg / len;
                if (target <= len || i + 2 == path.Count)
                {
                    return path[i] + dir * Mathf.Min(target, len);
                }
                target -= len;
            }
            return path[path.Count - 1];
        }

        /// <summary>
        /// 某個時刻（<paramref name="progress"/> 0..1）的姿態。<paramref name="path"/> 是進場口到迴轉點的折線（地圖座標），
        /// <paramref name="berth"/> 是泊位外框中心，<paramref name="finalHeading"/> 是停好後的船頭羅盤角，
        /// <paramref name="sideBerth"/>：舷靠（最後碰一下），否則錨泊。
        /// </summary>
        public static Pose At(float progress, List<Vector2> path, Vector2 berth, float finalHeading, bool sideBerth, bool shortVersion)
        {
            progress = Mathf.Clamp01(progress);
            Vector2 turn = path.Count > 0 ? path[path.Count - 1] : berth;
            if (progress <= TravelEnd)
            {
                float u = Mathf.SmoothStep(0f, 1f, progress / TravelEnd);
                float t = shortVersion ? Mathf.Lerp(1f - ShortTravelShare, 1f, u) : u;
                Vector2 pos = AlongPath(path, t, out Vector2 dir);
                return new Pose { pos = pos, angle = Wrap(Compass(dir) - finalHeading) };
            }
            AlongPath(path, 1f, out Vector2 lastDir);
            float startAngle = path.Count > 1 ? Wrap(Compass(lastDir) - finalHeading) : 0f;
            if (progress <= TurnEnd)
            {
                float u = Mathf.SmoothStep(0f, 1f, (progress - TravelEnd) / (TurnEnd - TravelEnd));
                return new Pose { pos = turn, angle = Mathf.Lerp(startAngle, 0f, u) };
            }
            if (progress <= SlideEnd)
            {
                float u = (progress - TurnEnd) / (SlideEnd - TurnEnd);
                float s = Mathf.SmoothStep(0f, 1f, u);
                if (sideBerth)
                {
                    // 擠過頭再回彈：前 80% 推到 1 + 擠壓，後 20% 彈回 1
                    s = u < 0.8f ? Mathf.SmoothStep(0f, 1f, u / 0.8f) * (1f + BumpCells / Mathf.Max(0.01f, (berth - turn).magnitude))
                                 : Mathf.Lerp(1f + BumpCells / Mathf.Max(0.01f, (berth - turn).magnitude), 1f, Mathf.SmoothStep(0f, 1f, (u - 0.8f) / 0.2f));
                }
                return new Pose { pos = Vector2.LerpUnclamped(turn, berth, s), angle = 0f };
            }
            return new Pose { pos = berth, angle = 0f };
        }

        /// <summary>停好後的輕微起伏（格）：越來越小，最後一刻歸零。</summary>
        public static float Bob(float progress, float seconds)
        {
            if (progress <= SlideEnd || progress >= 1f)
            {
                return 0f;
            }
            float u = (progress - SlideEnd) / (1f - SlideEnd);
            return Mathf.Sin(seconds * 2.4f) * 0.06f * (1f - u);
        }
    }
}
