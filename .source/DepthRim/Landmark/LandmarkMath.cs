using UnityEngine;

namespace DepthRim
{
    public enum LandmarkResult
    {
        Critical,
        Success,
        Failure
    }

    /// <summary>海洋地標的純規則（單元測試直接驗）：技能判定、聲納發現的頻率與上限。</summary>
    public static class LandmarkMath
    {
        public const float DefaultBaseChance = 0.5f;

        /// <summary>技能比難度每高一級，成功率加多少。</summary>
        public const float PerSkillLevel = 0.06f;

        public const float MinChance = 0.05f;

        public const float MaxChance = 0.95f;

        /// <summary>擲骰低於「成功率 × 這個比例」算大成功。</summary>
        public const float CriticalFraction = 0.25f;

        /// <summary>失敗時拿到的經驗值比例。</summary>
        public const float FailureXpFactor = 0.5f;

        /// <summary>聲納每格偵測範圍，每天平均發現幾個地標（主動聲納 6 格約 0.48 個、被動 3 格約 0.24 個）。</summary>
        public const float DiscoveriesPerDayPerRangeTile = 0.08f;

        /// <summary>一具聲納範圍內同時最多幾個地標。</summary>
        public const int MaxPerSonar = 3;

        /// <summary>全世界同時最多幾個地標。</summary>
        public const int MaxInWorld = 12;

        /// <summary>
        /// 成功率：基本率加上（技能 − 難度）× 6%，夾在 5%～95%，再乘上主角的工作能力（意識 × 操作，最低算 0.2）。
        /// </summary>
        public static float Chance(float baseChance, int skillLevel, int difficulty, float capability = 1f)
        {
            float chance = Mathf.Clamp(baseChance + (skillLevel - difficulty) * PerSkillLevel, MinChance, MaxChance);
            return chance * Mathf.Clamp(capability, 0.2f, 1f);
        }

        /// <summary><paramref name="roll"/> 是 [0, 1) 的亂數。</summary>
        public static LandmarkResult Resolve(float chance, float roll)
        {
            if (roll < chance * CriticalFraction)
            {
                return LandmarkResult.Critical;
            }
            return roll < chance ? LandmarkResult.Success : LandmarkResult.Failure;
        }

        public static float Xp(float xp, LandmarkResult result) => result == LandmarkResult.Failure ? xp * FailureXpFactor : xp;

        /// <summary>一具偵測範圍 <paramref name="range"/> 格的聲納每小時發現地標的機率。</summary>
        public static float DiscoveryChancePerHour(float range)
        {
            if (range <= 0f)
            {
                return 0f;
            }
            return Mathf.Clamp01(range * DiscoveriesPerDayPerRangeTile / 24f);
        }

        public static bool CanDiscover(int inRange, int inWorld) => inRange < MaxPerSonar && inWorld < MaxInWorld;
    }
}
