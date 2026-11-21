using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 可養殖的品種（§4.1）：連到既有的 <see cref="FishVariantDef"/>（顏色、名稱、魚獲、適溫）。
    /// 適溫直接用 variant 的 <see cref="FishVariantDef.temperature"/>，不重複填。
    /// </summary>
    public class AquacultureSpeciesDef : Def
    {
        public FishVariantDef variant;

        /// <summary>可以養在哪些網層。</summary>
        public List<NetLayer> netLayers = new List<NetLayer>();

        /// <summary>灣內（原版海岸地圖）可養。</summary>
        public bool coastal;

        /// <summary>100% 放養數（尾）。</summary>
        public int stockCount = 100;

        /// <summary>上市體重（kg）。</summary>
        public float harvestWeight = 2.5f;

        /// <summary>理想條件下從放養到上市的天數。</summary>
        public float growDays = 20f;

        /// <summary>每日餌率（體重的比例）。</summary>
        public float feedRate = 0.08f;

        public float o2Demand = 1f;

        /// <summary>破網時逃出去的倍率。</summary>
        public float escapeRisk = 1f;

        /// <summary>病害抗性：越高越不容易發病與被傳染。</summary>
        public float diseaseResistance = 1f;

        /// <summary>每尾的魚獲數；沒填就用 variant 的 catchCount。</summary>
        public IntRange yieldPerFish = IntRange.Zero;

        /// <summary>需要的研究（例如無鬚鱈／霜魚要深水箱網）。</summary>
        public ResearchProjectDef researchPrerequisite;

        public FloatRange Temperature => variant?.temperature ?? new FloatRange(0f, 20f);

        public IntRange YieldPerFish => yieldPerFish.max > 0 ? yieldPerFish : variant?.catchCount ?? IntRange.One;

        public float AverageYield => (YieldPerFish.min + YieldPerFish.max) * 0.5f;

        public ThingDef CatchDef => variant?.catchDef;

        public float TemperatureDistance(float temp)
        {
            return variant?.TemperatureDistance(temp) ?? 0f;
        }

        public bool Researched => researchPrerequisite == null || researchPrerequisite.IsFinished;

        /// <summary>放養規則（§4.3）：場地、網層、水溫 ±2 °C。符合回傳 null，否則回傳原因。</summary>
        public string CannotStockReason(CageVenue venue, NetLayer layer, float waterTemp)
        {
            if (venue == CageVenue.Bay && !coastal)
            {
                return "DepthRim.NetCage.Reason.OpenSeaOnly".Translate();
            }
            if (!netLayers.Contains(layer))
            {
                return "DepthRim.NetCage.Reason.WrongLayer".Translate();
            }
            if (!Researched)
            {
                return "DepthRim.NetCage.Reason.Research".Translate(researchPrerequisite.LabelCap);
            }
            if (TemperatureDistance(waterTemp) > 2f)
            {
                return "DepthRim.NetCage.Reason.Temperature".Translate(waterTemp.ToString("0.0"), Temperature.min.ToString("0"), Temperature.max.ToString("0"));
            }
            return null;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (variant == null)
            {
                yield return "variant is null";
            }
            if (netLayers.NullOrEmpty())
            {
                yield return "no netLayers";
            }
            if (coastal && !netLayers.Contains(NetLayer.Shallow))
            {
                yield return "coastal species must allow the shallow net";
            }
            if (stockCount <= 0 || harvestWeight <= 0f || growDays <= 0f || feedRate <= 0f || o2Demand <= 0f)
            {
                yield return "stockCount, harvestWeight, growDays, feedRate and o2Demand must be positive";
            }
            if (stockCount * harvestWeight > NetCageMath.CapacityKg + 0.01f)
            {
                yield return $"stockCount x harvestWeight ({stockCount * harvestWeight}) exceeds the cage capacity {NetCageMath.CapacityKg}";
            }
            if (yieldPerFish.max > 0 && yieldPerFish.min < 1)
            {
                yield return "yieldPerFish must be at least 1";
            }
        }
    }
}
