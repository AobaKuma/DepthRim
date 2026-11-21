using UnityEngine;

namespace DepthRim
{
    /// <summary>箱網附近的水中魚影（§5.6）：純視覺的密度與數量計算。</summary>
    public static class FishShadowField
    {
        public const int Interval = 450;
        public const float Radius = 12f;
        public const int MaxFlecks = 40;
        public const int PerBurst = 4;

        public static float PerCage(bool hasFish, bool hasFeed, bool bloom)
        {
            return 0.15f + (hasFish ? 0.25f : 0f) + (hasFeed ? 0.10f : 0f) + (bloom ? 0.10f : 0f);
        }

        public static float Clamp(float sum)
        {
            return Mathf.Clamp01(sum);
        }

        /// <summary>每 450 tick 生成幾個 fleck；不會讓總量超過 40。</summary>
        public static int FlecksToSpawn(float density, int alive)
        {
            int n = Mathf.CeilToInt(Mathf.Clamp01(density) * PerBurst);
            return Mathf.Max(0, Mathf.Min(n, MaxFlecks - alive));
        }
    }
}
