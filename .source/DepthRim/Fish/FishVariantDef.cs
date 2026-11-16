using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>通用魚類生物的體型：小、中、大三種貼圖（Textures/Thing/Fish/fish_s|m|l）。</summary>
    public enum FishSize
    {
        Small,
        Medium,
        Large
    }

    /// <summary>魚獲（原版 Odyssey 的魚）本身的大小，依每條的營養值：極小（孔雀魚、毒魚 0.05）、一般（0.25）、大型（0.5）。</summary>
    public enum FishCatchClass
    {
        Tiny,
        Normal,
        Large
    }

    /// <summary>
    /// 體型與魚獲大小的對應（2026-11-16 使用者要求「依體型來分配會出現的尺寸」）：
    /// 小型魚 → 極小或一般的魚獲；中型魚 → 一般的魚獲（數量較多）；大型魚 → 大型魚獲。
    /// </summary>
    public static class FishCatchSizes
    {
        public const float TinyMaxNutrition = 0.1f;

        public const float NormalMaxNutrition = 0.4f;

        public static FishCatchClass ClassOf(float nutrition)
        {
            if (nutrition <= TinyMaxNutrition)
            {
                return FishCatchClass.Tiny;
            }
            return nutrition <= NormalMaxNutrition ? FishCatchClass.Normal : FishCatchClass.Large;
        }

        public static FishCatchClass ClassOf(ThingDef catchDef)
        {
            return ClassOf(StatUtility.GetStatValueFromList(catchDef.statBases, StatDefOf.Nutrition, 0f));
        }

        public static bool Allows(FishSize size, FishCatchClass catchClass)
        {
            switch (size)
            {
                case FishSize.Small:
                    return catchClass == FishCatchClass.Tiny || catchClass == FishCatchClass.Normal;
                case FishSize.Medium:
                    return catchClass == FishCatchClass.Normal;
                default:
                    return catchClass == FishCatchClass.Large;
            }
        }
    }

    /// <summary>
    /// 魚的品種（2026-11-16 使用者設計）：海裡只有三種體型的通用魚，生成時依所在生態區、水深層級與水溫，
    /// 從同體型的品種裡抽一種，決定牠的顏色、名稱與死後變成的魚獲（<see cref="catchDef"/>，通常是 Odyssey 的魚）。
    /// 同一種魚獲可以出現在不同體型或顏色（例如小鱈魚、深海的鼠尾鱈都給鱈魚）。
    /// </summary>
    public class FishVariantDef : Def
    {
        public FishSize size = FishSize.Medium;

        /// <summary>乘在白色通用貼圖上的顏色。</summary>
        public Color color = Color.white;

        /// <summary>死亡時變成的魚獲。</summary>
        public ThingDef catchDef;

        /// <summary>魚獲數量。</summary>
        public IntRange catchCount = IntRange.One;

        /// <summary>適合的水溫（°C）。</summary>
        public FloatRange temperature = new FloatRange(-5f, 40f);

        /// <summary>只出現在這些生態區；空白 = 不限。</summary>
        public List<BiomeDef> biomes = new List<BiomeDef>();

        /// <summary>只出現在這些水深層級；空白 = 不限。</summary>
        public List<DepthTier> tiers = new List<DepthTier>();

        /// <summary>抽選權重。</summary>
        public float commonality = 1f;

        public bool HabitatMatches(BiomeDef biome, DepthTier? tier)
        {
            if (biomes.Count > 0 && (biome == null || !biomes.Contains(biome)))
            {
                return false;
            }
            if (tiers.Count > 0 && (!tier.HasValue || !tiers.Contains(tier.Value)))
            {
                return false;
            }
            return true;
        }

        /// <summary>離適溫範圍多遠（範圍內 = 0）。</summary>
        public float TemperatureDistance(float temp)
        {
            if (temp < temperature.min)
            {
                return temperature.min - temp;
            }
            if (temp > temperature.max)
            {
                return temp - temperature.max;
            }
            return 0f;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors())
            {
                yield return error;
            }
            if (catchDef == null)
            {
                yield return "catchDef is null";
            }
            else if (!FishCatchSizes.Allows(size, FishCatchSizes.ClassOf(catchDef)))
            {
                yield return $"{size} fish cannot drop {catchDef.defName} ({FishCatchSizes.ClassOf(catchDef)} catch): small fish drop tiny or normal fish, medium fish normal fish, large fish large fish";
            }
            if (catchCount.min < 1)
            {
                yield return "catchCount must be at least 1";
            }
            if (commonality <= 0f)
            {
                yield return "commonality must be positive";
            }
        }
    }

    /// <summary>品種抽選（純函式，可單元測試）。</summary>
    public static class FishVariantSelector
    {
        private static readonly List<FishVariantDef> tmp = new List<FishVariantDef>();

        /// <summary>
        /// 從 <paramref name="all"/> 裡挑一個同體型的品種：
        /// 先找棲地（生態區＋層級）與水溫都合的，依權重抽；水溫都不合就取棲地合、水溫最接近的；
        /// 棲地也都不合（例如牠被帶到別的地圖）就在同體型裡取水溫最接近的。<paramref name="roll"/> ∈ [0, 1)。
        /// </summary>
        public static FishVariantDef Choose(IEnumerable<FishVariantDef> all, FishSize size, BiomeDef biome, DepthTier? tier, float temperature, float roll)
        {
            tmp.Clear();
            foreach (FishVariantDef v in all)
            {
                if (v.size == size && v.HabitatMatches(biome, tier))
                {
                    tmp.Add(v);
                }
            }
            if (tmp.Count == 0)
            {
                foreach (FishVariantDef v in all)
                {
                    if (v.size == size)
                    {
                        tmp.Add(v);
                    }
                }
            }
            if (tmp.Count == 0)
            {
                return null;
            }
            float best = float.MaxValue;
            for (int i = 0; i < tmp.Count; i++)
            {
                best = Mathf.Min(best, tmp[i].TemperatureDistance(temperature));
            }
            tmp.RemoveAll(v => v.TemperatureDistance(temperature) > best + 0.001f);
            float total = 0f;
            for (int i = 0; i < tmp.Count; i++)
            {
                total += tmp[i].commonality;
            }
            float pick = Mathf.Clamp01(roll) * total;
            FishVariantDef result = tmp[tmp.Count - 1];
            for (int i = 0; i < tmp.Count; i++)
            {
                pick -= tmp[i].commonality;
                if (pick < 0f)
                {
                    result = tmp[i];
                    break;
                }
            }
            tmp.Clear();
            return result;
        }

        /// <summary>地圖上某處的品種：用地圖生態區、水深層級與目前的水溫（戶外溫度）。</summary>
        public static FishVariantDef ChooseFor(FishSize size, Map map)
        {
            BiomeDef biome = map?.Biome;
            DepthTier? tier = biome?.GetModExtension<WaterMapExtension>()?.depthTier;
            float temp = map != null ? map.mapTemperature.OutdoorTemp : 10f;
            return Choose(DefDatabase<FishVariantDef>.AllDefsListForReading, size, biome, tier, temp, Rand.Value);
        }
    }
}
