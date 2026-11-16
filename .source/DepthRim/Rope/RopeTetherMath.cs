using System;
using UnityEngine;

namespace DepthRim
{
    /// <summary>扣繩的優先順序（數字小的先扣）：殖民者（含奴隸）＞機械體＞囚犯＞動物。</summary>
    public enum TetherRank
    {
        Colonist = 0,
        Mech = 1,
        Prisoner = 2,
        Animal = 3,
    }

    /// <summary>
    /// 安全繩錨點的純計算（不需要遊戲迴圈，單元測試直接跑）：繩長範圍、格子遮罩的索引、繩子會不會被牆擋住、
    /// 扣繩排序、懸垂曲線、惡劣海況落海的平均間隔。設計見 Docs/安全繩錨點_機制設計.md。
    /// </summary>
    public static class RopeTetherMath
    {
        /// <summary>繩長（格）。</summary>
        public const int Reach = 9;

        public const float RopeLength = Reach;

        /// <summary>一個錨點最多同時扣幾條繩子（保護上限＝畫面上限）。</summary>
        public const int MaxTethers = 2;

        /// <summary>格子遮罩的邊長：以錨點為中心的 (2 × 繩長 + 1) 見方。</summary>
        public const int MaskSide = 2 * Reach + 1;

        public const int MaskSize = MaskSide * MaskSide;

        /// <summary>繩子鬆的時候，中點最多往下垂幾格。</summary>
        public const float MaxSag = 0.6f;

        /// <summary>一條繩子畫成幾段。</summary>
        public const int Segments = 12;

        /// <summary>在繩長內：直線距離 ≤ <see cref="Reach"/>。</summary>
        public static bool InReach(int dx, int dz)
        {
            return dx * dx + dz * dz <= Reach * Reach;
        }

        /// <summary>相對錨點的位移在遮罩裡的索引；在遮罩外回傳 -1。</summary>
        public static int MaskIndex(int dx, int dz)
        {
            if (dx < -Reach || dx > Reach || dz < -Reach || dz > Reach)
            {
                return -1;
            }
            return (dz + Reach) * MaskSide + (dx + Reach);
        }

        /// <summary>
        /// 從錨點 (0,0) 拉到 (dx,dz) 的繩子有沒有被擋住：沿 Bresenham 直線逐格看，不含兩端。
        /// <paramref name="blocked"/> 收相對錨點的位移，回傳那一格擋不擋繩子（牆擋、門不擋，見 §2.2）。
        /// </summary>
        public static bool RopePasses(int dx, int dz, Func<int, int, bool> blocked)
        {
            if (dx == 0 && dz == 0)
            {
                return true;
            }
            int x = 0, z = 0;
            int ax = Math.Abs(dx), az = Math.Abs(dz);
            int sx = Math.Sign(dx), sz = Math.Sign(dz);
            int err = ax - az;
            while (true)
            {
                int e2 = 2 * err;
                if (e2 > -az)
                {
                    err -= az;
                    x += sx;
                }
                if (e2 < ax)
                {
                    err += ax;
                    z += sz;
                }
                if (x == dx && z == dz)
                {
                    return true;
                }
                if (blocked(x, z))
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 填好一個錨點的格子遮罩：一格是 true =「在繩長內，而且拉過去不會被擋住」。
        /// 錨點自己那一格永遠是 true。回傳 true 的格數。
        /// </summary>
        public static int BuildMask(bool[] mask, Func<int, int, bool> blocked)
        {
            int count = 0;
            for (int dz = -Reach; dz <= Reach; dz++)
            {
                for (int dx = -Reach; dx <= Reach; dx++)
                {
                    bool ok = InReach(dx, dz) && (dx == 0 && dz == 0 || !blocked(dx, dz) && RopePasses(dx, dz, blocked));
                    mask[MaskIndex(dx, dz)] = ok;
                    if (ok)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>扣繩排序：先比優先順序，同一類近的先。</summary>
        public static int Compare(TetherRank rankA, int distSqA, TetherRank rankB, int distSqB)
        {
            int c = ((int)rankA).CompareTo((int)rankB);
            return c != 0 ? c : distSqA.CompareTo(distSqB);
        }

        /// <summary>鬆緊度：0 = 拉直（距離 = 繩長），1 = 完全鬆（距離 0）。</summary>
        public static float Slack(float distance)
        {
            return Mathf.Clamp01(1f - distance / RopeLength);
        }

        /// <summary>中點往下垂多少格。</summary>
        public static float Sag(float distance)
        {
            return MaxSag * Slack(distance);
        }

        /// <summary>
        /// 繩上第 t（0..1）處的位置：兩端直線內插，中段用拋物線往南（畫面往下）垂 <paramref name="sag"/>、
        /// 加上 <paramref name="drift"/>（海水擺動或水流）。兩端不動。
        /// </summary>
        public static Vector3 Point(Vector3 from, Vector3 to, float t, float sag, Vector3 drift)
        {
            float bulge = 4f * t * (1f - t);
            Vector3 p = Vector3.Lerp(from, to, t);
            p.z -= sag * bulge;
            p += drift * bulge;
            return p;
        }

        // ── 惡劣海況（§3.2）──

        /// <summary>天氣倍率到這裡算惡劣海況。</summary>
        public const float RoughSeaFactor = 1.8f;

        /// <summary>沒扣繩的人平均多久被浪捲走（遊戲小時）的取樣點：倍率 → MTB。</summary>
        private static readonly float[] MtbFactors = { 1.8f, 2.0f, 2.5f };

        private static readonly float[] MtbHours = { 12f, 6f, 1.5f };

        /// <summary>離開放海面 2 格以上（甲板中央）的倍率。</summary>
        public const float InnerDeckFactor = 0.4f;

        public const float DownedFactor = 3f;

        public const float LowMovingFactor = 1.5f;

        /// <summary>移動能力低於這個值算行動不便。</summary>
        public const float LowMoving = 0.5f;

        public static bool IsRoughSea(float weatherFactor)
        {
            return weatherFactor >= RoughSeaFactor;
        }

        /// <summary>
        /// 艙外、沒扣繩的人平均多久被浪捲走（遊戲小時）。平靜時回傳正無限大。
        /// 取樣點之間對「機率率」（MTB 的倒數）線性內插，超過最後一點時照最後一點。
        /// </summary>
        public static float RoughSeaMtbHours(float weatherFactor, bool nearEdge, bool downed, bool lowMoving)
        {
            if (!IsRoughSea(weatherFactor))
            {
                return float.PositiveInfinity;
            }
            float rate;
            if (weatherFactor >= MtbFactors[MtbFactors.Length - 1])
            {
                rate = 1f / MtbHours[MtbHours.Length - 1];
            }
            else
            {
                int i = 0;
                while (weatherFactor > MtbFactors[i + 1])
                {
                    i++;
                }
                float t = (weatherFactor - MtbFactors[i]) / (MtbFactors[i + 1] - MtbFactors[i]);
                rate = Mathf.Lerp(1f / MtbHours[i], 1f / MtbHours[i + 1], t);
            }
            float mult = (nearEdge ? 1f : InnerDeckFactor) * (downed ? DownedFactor : 1f) * (lowMoving ? LowMovingFactor : 1f);
            return 1f / (rate * mult);
        }

        /// <summary>每 <paramref name="intervalTicks"/> 擲一次時，一次就被捲走的機率（和原版 MTB 判定相同的指數式）。</summary>
        public static float ChancePerCheck(float mtbHours, int intervalTicks)
        {
            if (float.IsInfinity(mtbHours) || mtbHours <= 0f)
            {
                return mtbHours <= 0f ? 1f : 0f;
            }
            return (float)(1.0 - Math.Exp(-intervalTicks / (mtbHours * 2500.0)));
        }
    }
}
