using UnityEngine;

namespace DepthRim
{
    /// <summary>箱網的網層（Docs/箱網養殖_內容設計.md §3.2）。</summary>
    public enum NetLayer
    {
        Shallow,
        Midwater,
        Deep
    }

    /// <summary>箱網所在的場地：原版海岸地圖的灣內，或 DepthRim 海面地圖的外海（§0.3）。</summary>
    public enum CageVenue
    {
        Bay,
        OpenSea
    }

    /// <summary>
    /// 箱網的數值模型（§5）：水溫、溶氧、成長、飼料、死亡、收成。全部是純函式，只吃數值，不讀地圖。
    /// </summary>
    public static class NetCageMath
    {
        // ── 容量與飼料（§4.2、§13）──
        public const float CapacityKg = 300f;
        public const float MaxDensity = 1.5f;
        public const float FeedCapacity = 240f;
        public const float RefillFraction = 0.4f;

        /// <summary>放養時的初始體重比例（上市體重的 2%）。</summary>
        public const float StartWeightFraction = 0.02f;

        // ── 水溫（§5.1）──
        public const float BayDamping = 0.55f;

        /// <summary>灣內的設計水深：淺水 10 m、深水 50 m（§0.3）。</summary>
        public const float BayShallowDepth = 10f;
        public const float BayDeepDepth = 50f;

        /// <summary>灣內水溫：海面年均水溫＋氣溫距平 × 0.55，夾在冰點與 29.5 °C。</summary>
        public static float BayWaterTemperature(float airAnnual, float outdoorTemp)
        {
            float annual = OceanTemperature.SurfaceWater(airAnnual);
            return Mathf.Clamp(annual + (outdoorTemp - airAnnual) * BayDamping, OceanTemperature.FreezingPoint, OceanTemperature.MaxSurfaceWater);
        }

        /// <summary>外海網袋中點的季節阻尼：0.45·e^(−d/80) + 0.15·e^(−d/15)。</summary>
        public static float OpenSeaDamping(float netDepth)
        {
            return 0.45f * Mathf.Exp(-netDepth / 80f) + 0.15f * Mathf.Exp(-netDepth / 15f);
        }

        /// <summary>外海水溫：該深度的年均水溫＋氣溫距平 × 阻尼（冰下沒有季節）。</summary>
        public static float OpenSeaWaterTemperature(float netDepth, float airAnnual, float outdoorTemp, OceanTemperature.Kind kind)
        {
            float annual = OceanTemperature.AnnualMean(netDepth, airAnnual, kind);
            float damping = kind == OceanTemperature.Kind.Subglacial ? 0f : OpenSeaDamping(netDepth);
            return Mathf.Max(OceanTemperature.FreezingPoint, annual + (outdoorTemp - airAnnual) * damping);
        }

        // ── 溶氧（§5.2）──
        public const float NeighborPenalty = 0.08f;
        public const float MaxNeighborPenalty = 0.24f;
        public const float AeratorBonus = 0.15f;
        public const int MaxAerators = 2;
        public const float BloomPenalty = 0.18f;
        public const float BayWeatherPenalty = 0.08f;

        public const float OxygenWarning = 0.50f;
        public const float OxygenLow = 0.35f;
        public const float OxygenLethal = 0.15f;

        /// <summary>低氧持續多久開始停長／缺氧死亡（6 小時）。</summary>
        public const int LowOxygenTicks = 6 * 2500;

        public static float BaseOxygen(float netDepth)
        {
            return 0.95f - 0.19f * Mathf.Clamp01(netDepth / 500f);
        }

        public static float WarmPenalty(float waterTemp)
        {
            return Mathf.Clamp((waterTemp - 18f) / 40f, 0f, 0.15f);
        }

        public static float Demand(float biomass, float o2Demand)
        {
            return biomass / CapacityKg * o2Demand * 0.25f;
        }

        public static float NeighborsPenalty(int neighbors)
        {
            return Mathf.Min(MaxNeighborPenalty, NeighborPenalty * Mathf.Max(0, neighbors));
        }

        public static float Aeration(int aerators)
        {
            return AeratorBonus * Mathf.Clamp(aerators, 0, MaxAerators);
        }

        /// <param name="extra">其他加減（灣內天氣、原版氣候事件、洋流）。</param>
        public static float Oxygen(float netDepth, float waterTemp, float biomass, float o2Demand, int neighbors, int aerators, bool bloom, float extra = 0f)
        {
            float o = BaseOxygen(netDepth) - WarmPenalty(waterTemp) - Demand(biomass, o2Demand) - NeighborsPenalty(neighbors) + Aeration(aerators) - (bloom ? BloomPenalty : 0f) + extra;
            return Mathf.Clamp01(o);
        }

        // ── 成長（§5.3）──
        public static float Smoothstep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        public static float TempFactor(float tempDistance)
        {
            return Mathf.Clamp01(1f - Mathf.Max(0f, tempDistance) / 6f);
        }

        /// <param name="sustainedLow">溶氧低於 0.35 已持續 6 小時：直接停長。</param>
        public static float O2Factor(float oxygen, bool sustainedLow = false)
        {
            if (sustainedLow)
            {
                return 0f;
            }
            return 0.2f + 0.8f * Smoothstep(0.25f, 0.70f, oxygen);
        }

        public static float Density(float biomass)
        {
            return biomass / CapacityKg;
        }

        public static float DensityFactor(float density)
        {
            return 1f - 0.40f * Mathf.Clamp01((density - 0.8f) / 0.7f);
        }

        public const float BayStormGrowth = 0.8f;

        /// <summary>灣內的原版天氣：強風或大雨。</summary>
        public static bool BayRoughWeather(float windSpeedFactor, float rainRate)
        {
            return windSpeedFactor >= 1.2f || rainRate >= 0.5f;
        }

        /// <summary>每日成長（kg／尾）。<paramref name="otherFactor"/> 是風暴、氣候事件與洋流的乘積。</summary>
        public static float GrowthPerDay(float harvestWeight, float growDays, float tempDistance, float oxygen, bool sustainedLow, float density, bool fed, float otherFactor)
        {
            if (!fed || growDays <= 0f)
            {
                return 0f;
            }
            float baseRate = (harvestWeight - StartWeightFraction * harvestWeight) / growDays;
            return baseRate * TempFactor(tempDistance) * O2Factor(oxygen, sustainedLow) * DensityFactor(density) * otherFactor;
        }

        /// <summary>每日飼料（Kibble 個數，1 個 = 1 kg 投餌）。</summary>
        public static float DailyFeed(float biomass, float feedRate, float feedRateSetting)
        {
            return biomass * feedRate * feedRateSetting;
        }

        // ── 死亡（§5.4）──
        public const float BaseMortality = 0.0015f;
        public const int OverripeGraceDays = 3;

        public static float DailyMortality(float oxygen, float density, float tempDistance, float overripeDays)
        {
            float m = BaseMortality
                      + 0.02f * Mathf.Max(0f, OxygenLow - oxygen) / OxygenLow
                      + (density > 0.9f ? 0.002f : 0f)
                      + 0.01f * Mathf.Max(0f, tempDistance - 6f) / 6f;
            if (overripeDays > 0f)
            {
                m += 0.002f;
            }
            return m;
        }

        /// <summary>過熟天數：達到上市體重後超過 3 天的部分。</summary>
        public static float OverripeDays(float daysAtHarvestWeight)
        {
            return Mathf.Max(0f, daysAtHarvestWeight - OverripeGraceDays);
        }

        // ── 收成（§7）──
        public const float HarvestReady = 0.9f;
        public const float EmergencyMin = 0.5f;
        public const float FailedFraction = 0.1f;

        /// <summary>過熟的魚獲倍率：每天 −10%，最低 50%。</summary>
        public static float OverripeFactor(float overripeDays)
        {
            return Mathf.Max(0.5f, 1f - 0.1f * Mathf.Max(0f, overripeDays));
        }

        /// <summary>
        /// 魚獲數：正常收成每尾 <paramref name="yieldPerFish"/>；體重未達 90% 的緊急收成按體重比折算（每尾至少 1）；過熟再乘倍率。
        /// </summary>
        public static int HarvestYield(int count, float yieldPerFish, float weightRatio, float overripeDays)
        {
            if (count <= 0 || weightRatio < EmergencyMin)
            {
                return 0;
            }
            float perFish = weightRatio >= HarvestReady ? yieldPerFish : Mathf.Max(1f, Mathf.Round(yieldPerFish * weightRatio));
            return Mathf.Max(0, Mathf.RoundToInt(count * perFish * OverripeFactor(overripeDays)));
        }

        // ── 破網（§6.1）──
        public const float BreachIntegrity = 0.5f;
        public const float DangerIntegrity = 0.2f;

        /// <summary>每次海況傷害後的破網機率（外海）。</summary>
        public static float StormBreachChance(float integrity)
        {
            if (integrity >= BreachIntegrity)
            {
                return 0f;
            }
            return integrity < DangerIntegrity ? 0.15f : 0.05f;
        }

        /// <summary>每日的破網機率（灣內 1%；外海 2%，危險區 15%）。</summary>
        public static float DailyBreachChance(float integrity, CageVenue venue)
        {
            if (integrity >= BreachIntegrity)
            {
                return 0f;
            }
            if (venue == CageVenue.Bay)
            {
                return 0.01f;
            }
            return integrity < DangerIntegrity ? 0.15f : 0.02f;
        }

        public static readonly UnityEngine.Vector2 BreachLossRange = new Vector2(0.05f, 0.15f);

        public const int MaxEscapePawns = 10;
    }
}
