using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 每個水深層級（<see cref="DepthTier"/>）一組水體視覺參數（設計稿 §3.3、實作指南 C.1）。
    /// 數值寫在 <c>1.7/Defs/Rendering/WaterVisualTiers.xml</c>，不需要重新編譯就能調；
    /// 材質由 <see cref="WaterTierMaterials"/> 依這裡的欄位建立。
    ///
    /// <para>查表用 <see cref="For"/>（依 tier 建快取，Def 重新載入時自動重建）。
    /// <see cref="DepthRimDefOf"/> 不需要逐一列出，避免為四個層級各加一個欄位。</para>
    /// </summary>
    public class WaterVisualTierDef : Def
    {
        /// <summary>對應的層級（唯一，四個層級各一個 Def）。</summary>
        public DepthTier tier;

        /// <summary>已探索的水色。</summary>
        public Color waterColor = new Color(0.06f, 0.24f, 0.40f);

        /// <summary>深處的水色（沉積霧氣往這裡拉、深水區的底）。</summary>
        public Color deepColor = new Color(0.02f, 0.07f, 0.15f);

        /// <summary>迷霧區的水色。</summary>
        public Color fogColor = new Color(0.015f, 0.05f, 0.11f);

        /// <summary>光照充足時的覆蓋程度（0–1）。</summary>
        public float clearAlpha = 0.15f;

        /// <summary>完全沒光時的覆蓋程度（0–1）。</summary>
        public float murkAlpha = 0.85f;

        /// <summary>
        /// 日光能穿多深（0–1）：<see cref="FloodingMath.CombineLight"/> 會把天空光乘上這個值。
        /// 淺海 1（有日光就算有光），越深越小，深海在白天也不會變透明。人造光不受影響。
        /// </summary>
        public float ambientReach = 1f;

        /// <summary>波紋取樣尺度：1 / 尺度 = 重複週期（格）。0 = 沒有波紋。</summary>
        public float rippleScale = 0.03f;

        /// <summary>波紋與光紋強度（0–1）。0 時著色器跳過波紋。</summary>
        public float rippleStrength = 0.8f;

        /// <summary>光紋強度（0–1）。</summary>
        public float causticStrength = 0.2f;

        /// <summary>淺海光柱強度（0–1），其他層級 0。</summary>
        public float lightShaftStrength;

        /// <summary>海洋雪密度（0–1）。</summary>
        public float snowDensity;

        /// <summary>海洋雪下沉速度倍率。</summary>
        public float snowSpeed = 1f;

        /// <summary>生物光密度（0–1）。</summary>
        public float biolumDensity;

        /// <summary>生物光顏色（稀疏冷色）。</summary>
        public Color biolumColor = new Color(0.35f, 0.90f, 0.85f);

        /// <summary>海床沉積霧氣強度（0–1）。</summary>
        public float hazeStrength;

        /// <summary>變溫層假扭曲強度（0–1）。</summary>
        public float wobbleStrength;

        /// <summary>艙室層是否套用 D 階段效果（預設 false，見指南 E）。</summary>
        public bool compartmentEffects;

        private static WaterVisualTierDef[] byTier;

        private static int cachedDefCount = -1;

        /// <summary>層級對應的 Def；沒有定義時為 null（呼叫端用預設值）。</summary>
        public static WaterVisualTierDef For(DepthTier tier)
        {
            EnsureCache();
            int index = (int)tier;
            return byTier != null && index >= 0 && index < byTier.Length ? byTier[index] : null;
        }

        /// <summary>四個層級是否都有 Def（測試與除錯用）。</summary>
        public static bool AllTiersDefined()
        {
            for (DepthTier t = DepthTier.Shallow; t <= DepthTier.Seabed; t++)
            {
                if (For(t) == null)
                {
                    return false;
                }
            }
            return true;
        }

        private static void EnsureCache()
        {
            int count = DefDatabase<WaterVisualTierDef>.DefCount;
            if (byTier != null && cachedDefCount == count)
            {
                return;
            }
            cachedDefCount = count;
            byTier = new WaterVisualTierDef[4];
            foreach (WaterVisualTierDef def in DefDatabase<WaterVisualTierDef>.AllDefs)
            {
                int index = (int)def.tier;
                if (index >= 0 && index < byTier.Length && byTier[index] == null)
                {
                    byTier[index] = def;
                }
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors())
            {
                yield return error;
            }
            if (DefDatabase<WaterVisualTierDef>.AllDefsListForReading.Count(d => d.tier == tier) > 1)
            {
                yield return "duplicate WaterVisualTierDef for tier " + tier;
            }
            for (DepthTier t = DepthTier.Shallow; t <= DepthTier.Seabed; t++)
            {
                if (!DefDatabase<WaterVisualTierDef>.AllDefsListForReading.Any(d => d.tier == t))
                {
                    yield return "missing WaterVisualTierDef for tier " + t;
                }
            }
            string[] ranges =
            {
                CheckRange("clearAlpha", clearAlpha, 0f, 1f),
                CheckRange("murkAlpha", murkAlpha, 0f, 1f),
                CheckRange("ambientReach", ambientReach, 0f, 1f),
                CheckRange("rippleStrength", rippleStrength, 0f, 1f),
                CheckRange("causticStrength", causticStrength, 0f, 1f),
                CheckRange("lightShaftStrength", lightShaftStrength, 0f, 1f),
                CheckRange("snowDensity", snowDensity, 0f, 1f),
                CheckRange("biolumDensity", biolumDensity, 0f, 1f),
                CheckRange("hazeStrength", hazeStrength, 0f, 1f),
                CheckRange("wobbleStrength", wobbleStrength, 0f, 1f)
            };
            foreach (string range in ranges)
            {
                if (range != null)
                {
                    yield return range;
                }
            }
            if (rippleScale < 0f)
            {
                yield return "rippleScale must not be negative";
            }
            if (snowSpeed < 0f)
            {
                yield return "snowSpeed must not be negative";
            }
        }

        private static string CheckRange(string field, float value, float min, float max)
        {
            return value < min || value > max ? field + " " + value + " is outside " + min + "–" + max : null;
        }
    }
}
