using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 掛在 GeneDef 上的海洋基因參數（基因設計稿 §1.1）。沒寫的欄位都是「沒有影響」。
    /// 能用原版欄位（statOffsets、conditionalStatAffecters、disablesNeeds…）做到的效果寫在 GeneDef 本身，不放這裡。
    /// </summary>
    public class GeneExtension_DepthRim : DefModExtension
    {
        // ── 呼吸（§2） ──
        public float drowningFactor = 1f;

        public float airUseFactor = 1f;

        /// <summary>鰓：沒入水中不溺水、不耗氣，但離水會乾燥（<see cref="Gene_Gills"/>）。</summary>
        public bool gills;

        // ── 耐壓（§4）：負數 = 沒有指定 ──
        public float pressureTolerance = -1f;

        public float pressureSpeedFactor = 1f;

        public int pressureMaxLevel = -1;

        public float barotraumaFactor = 1f;

        /// <summary>水壓傷害的自然消退倍率（抗減壓病）。</summary>
        public float barotraumaRecoveryFactor = 1f;

        // ── 暈船（§3） ──
        /// <summary>不會在長時間暴露後習慣（易暈船）。</summary>
        public bool noSeasickAcclimation;

        // ── 畏光（§5）：1 畏光、2 強烈畏光 ──
        public int photophobia;
    }

    /// <summary>
    /// 彙整角色身上有效的基因（<see cref="Gene.Active"/>）的 <see cref="GeneExtension_DepthRim"/>。
    /// 結果依角色快取 <see cref="CacheTicks"/> ticks（水密門的尋路判斷會頻繁呼叫），基因變動最多晚這麼久生效。
    /// 沒有 Biotech 時 <c>pawn.genes</c> 只有內生基因，結果就是 <see cref="MarineGeneProfile.Baseline"/>。
    /// </summary>
    public static class DepthRimGenes
    {
        public const int CacheTicks = 250;

        private struct Entry
        {
            public int tick;

            public MarineGeneProfile profile;
        }

        private static readonly Dictionary<int, Entry> cache = new Dictionary<int, Entry>();

        public static MarineGeneProfile Of(Pawn pawn)
        {
            if (pawn?.genes == null)
            {
                return MarineGeneProfile.Baseline;
            }
            int now = Find.TickManager?.TicksGame ?? 0;
            if (cache.TryGetValue(pawn.thingIDNumber, out Entry e) && now >= e.tick && now - e.tick < CacheTicks)
            {
                return e.profile;
            }
            MarineGeneProfile profile = Compute(pawn);
            if (cache.Count > 4000)
            {
                cache.Clear();
            }
            cache[pawn.thingIDNumber] = new Entry { tick = now, profile = profile };
            return profile;
        }

        /// <summary>基因變了（測試、除錯、基因植入）時立刻重算。</summary>
        public static void Invalidate(Pawn pawn)
        {
            if (pawn != null)
            {
                cache.Remove(pawn.thingIDNumber);
            }
        }

        public static void ClearCache()
        {
            cache.Clear();
        }

        public static PressureProfile PressureOf(Pawn pawn) => Of(pawn).pressure;

        public static MarineGeneProfile Compute(Pawn pawn)
        {
            MarineGeneProfile p = MarineGeneProfile.Baseline;
            List<Gene> genes = pawn?.genes?.GenesListForReading;
            if (genes == null)
            {
                return p;
            }
            float tolerance = -1f;
            for (int i = 0; i < genes.Count; i++)
            {
                Gene gene = genes[i];
                if (gene?.def == null || !gene.Active)
                {
                    continue;
                }
                GeneExtension_DepthRim ext = gene.def.GetModExtension<GeneExtension_DepthRim>();
                if (ext == null)
                {
                    continue;
                }
                p.drowningFactor *= ext.drowningFactor;
                p.airUseFactor *= ext.airUseFactor;
                if (ext.gills)
                {
                    p.gills = true;
                    p.drowningFactor = 0f;
                    p.airUseFactor = 0f;
                }
                if (ext.pressureTolerance >= 0f)
                {
                    // 同類基因互斥；萬一有兩個，取比較極端的（離基準遠的）
                    tolerance = tolerance < 0f || Mathf.Abs(ext.pressureTolerance - 1f) > Mathf.Abs(tolerance - 1f) ? ext.pressureTolerance : tolerance;
                }
                p.pressure.speed *= ext.pressureSpeedFactor;
                if (ext.pressureMaxLevel >= 0)
                {
                    p.pressure.maxLevel = Mathf.Min(p.pressure.maxLevel, ext.pressureMaxLevel);
                }
                p.pressure.damageFactor *= ext.barotraumaFactor;
                p.barotraumaRecoveryFactor *= ext.barotraumaRecoveryFactor;
                p.noSeasickAcclimation |= ext.noSeasickAcclimation;
                p.photophobia = Mathf.Max(p.photophobia, ext.photophobia);
            }
            if (tolerance >= 0f)
            {
                p.pressure.tolerance = tolerance;
            }
            return p;
        }

        /// <summary>角色有沒有這個基因而且有效（被覆蓋的基因不算）。</summary>
        public static bool HasActive(Pawn pawn, GeneDef def)
        {
            return def != null && pawn?.genes != null && pawn.genes.HasActiveGene(def);
        }
    }
}
