using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海上的人（基因設計稿 §7）：突擊艇與海盜潛艇的船員有機會是水手或深潛者；沉船裡有機會找到海洋基因包。
    /// 只在 Biotech 啟用時作用（沒有 Biotech 時基因系統不給玩家用，也沒有基因包）。
    /// </summary>
    public static class MarineXenotypes
    {
        public const float MarinerChance = 0.3f;

        public const float DeepdiverChance = 0.1f;

        public const float ShipwreckGenepackChance = 0.4f;

        public static XenotypeDef Mariner => DefDatabase<XenotypeDef>.GetNamedSilentFail("DepthRim_Mariner");

        public static XenotypeDef Deepdiver => DefDatabase<XenotypeDef>.GetNamedSilentFail("DepthRim_Deepdiver");

        /// <summary>海洋基因分類（<c>DepthRim_Marine</c>）裡的基因。</summary>
        public static IEnumerable<GeneDef> MarineGenes =>
            DefDatabase<GeneDef>.AllDefs.Where(g => g.displayCategory != null && g.displayCategory.defName == "DepthRim_Marine");

        /// <summary>
        /// 船員：原本是基準人類的，30% 改成水手、10% 改成深潛者。派系自己的種族（例如海盜裡的豬人）不動。
        /// </summary>
        public static void TryAssignCrew(Pawn pawn)
        {
            if (!ModsConfig.BiotechActive || pawn?.genes == null || !pawn.RaceProps.Humanlike)
            {
                return;
            }
            if (pawn.genes.Xenotype != XenotypeDefOf.Baseliner)
            {
                return;
            }
            float roll = Rand.Value;
            XenotypeDef xenotype = roll < DeepdiverChance ? Deepdiver : (roll < DeepdiverChance + MarinerChance ? Mariner : null);
            if (xenotype == null)
            {
                return;
            }
            pawn.genes.SetXenotype(xenotype);
            DepthRimGenes.Invalidate(pawn);
        }

        /// <summary>沉船戰利品：40% 機會放一個有 1–2 個海洋基因（不含 archite）的基因包。</summary>
        public static Thing TryMakeShipwreckGenepack()
        {
            if (!ModsConfig.BiotechActive || !Rand.Chance(ShipwreckGenepackChance))
            {
                return null;
            }
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("Genepack");
            List<GeneDef> pool = MarineGenes.Where(g => g.biostatArc <= 0 && g.canGenerateInGeneSet).ToList();
            if (def == null || pool.Count == 0)
            {
                return null;
            }
            Genepack pack = ThingMaker.MakeThing(def) as Genepack;
            if (pack == null)
            {
                return null;
            }
            List<GeneDef> genes = pool.InRandomOrder().Take(Rand.RangeInclusive(1, 2)).ToList();
            pack.Initialize(genes);
            return pack;
        }
    }
}
