using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 暈船（基因設計稿 §3）：嚴重度往「暴露量 × 暈船敏感度」移動（<see cref="SeasicknessMath"/>），自己每 250 ticks 結算。
    /// 低於「頭暈」時不顯示；長時間沒有暴露、嚴重度又回到 0 時移除（同時忘掉習慣）。
    /// </summary>
    public class Hediff_Seasick : Hediff
    {
        public const int UpdateInterval = 250;

        public const float RestingSeverity = 0.001f;

        private int pending;

        private int exposedTicks;

        private int awayTicks;

        private float exposure;

        public float Exposure => exposure;

        public int ExposedTicks => exposedTicks;

        public bool Acclimated => SeasicknessMath.Acclimated(exposedTicks, !DepthRimGenes.Of(pawn).noSeasickAcclimation);

        public override bool Visible => Severity >= SeasicknessMath.QueasySeverity;

        public override bool ShouldRemove => Severity <= RestingSeverity + 0.0001f && exposure <= 0f && exposedTicks == 0;

        public override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            pending += delta;
            if (pending >= UpdateInterval)
            {
                int ticks = pending;
                pending = 0;
                Advance(ticks);
            }
        }

        public void Advance(int ticks)
        {
            exposure = SeasicknessExposure.For(pawn);
            SeasicknessMath.Track(ref exposedTicks, ref awayTicks, exposure > 0f, ticks);
            float sensitivity = pawn.GetStatValue(DepthRimDefOf.DepthRim_SeasicknessSensitivity);
            float target = SeasicknessMath.Target(exposure, sensitivity, Acclimated);
            bool asleep = !pawn.Awake();
            float next = SeasicknessMath.Step(Severity, target, ticks, asleep);
            Severity = next < RestingSeverity ? RestingSeverity : next;
        }

        public override string TipStringExtra
        {
            get
            {
                string s = base.TipStringExtra;
                string line = "DepthRim.Seasick.Exposure".Translate(exposure.ToStringPercent());
                if (Acclimated)
                {
                    line += "\n" + "DepthRim.Seasick.Acclimated".Translate();
                }
                return s.NullOrEmpty() ? line : s + "\n" + line;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref pending, "pending", 0);
            Scribe_Values.Look(ref exposedTicks, "exposedTicks", 0);
            Scribe_Values.Look(ref awayTicks, "awayTicks", 0);
            Scribe_Values.Look(ref exposure, "exposure", 0f);
        }
    }

    /// <summary>暈船的暴露量從哪裡來（基因設計稿 §3.1）。</summary>
    public static class SeasicknessExposure
    {
        /// <summary>測試用：不是 null 時取代地圖算出的暴露量。</summary>
        public static float? debugExposure;

        public static float For(Pawn pawn)
        {
            if (pawn == null || !Applies(pawn))
            {
                return 0f;
            }
            return Of(pawn.MapHeld);
        }

        /// <summary>這張地圖上的暴露量：海面看擺動倍率，航行中的潛艇看上浮／下潛與淺海航行。</summary>
        public static float Of(Map map)
        {
            if (map == null)
            {
                return 0f;
            }
            if (debugExposure.HasValue)
            {
                return debugExposure.Value;
            }
            if (SeaLevels.IsAnyLevel(map))
            {
                Map sea = SeaLevels.SeaLevelOf(map) ?? map;
                return SeasicknessMath.SeaExposure(OverboardHazard.SeaFactor(sea));
            }
            if (map.Parent is WorldObject_Submarine sub)
            {
                bool shallow = VoyageOverboard.Underway(sub) && sub.CurrentDepthTier == DepthTier.Shallow;
                return SeasicknessMath.VoyageExposure(sub.Diving, shallow);
            }
            return 0f;
        }

        /// <summary>會暈船的：血肉的人形（動物、機械體不暈；基因設計稿 §9 Q4）。</summary>
        public static bool Applies(Pawn pawn)
        {
            return !pawn.Dead && pawn.RaceProps.Humanlike && pawn.RaceProps.IsFlesh;
        }
    }

    /// <summary>
    /// 有暴露的地圖上，替還沒有暈船 hediff 的人加上（之後由 hediff 自己結算）。沒有暴露的地圖什麼都不做。
    /// </summary>
    public class MapComponent_Seasickness : MapComponent
    {
        public const int Interval = 250;

        private static readonly List<Pawn> tmp = new List<Pawn>();

        public MapComponent_Seasickness(Map map) : base(map)
        {
        }

        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % Interval != 37)
            {
                return;
            }
            float exposure = SeasicknessExposure.Of(map);
            if (exposure <= 0f)
            {
                return;
            }
            tmp.Clear();
            tmp.AddRange(map.mapPawns.AllHumanlikeSpawned);
            for (int i = 0; i < tmp.Count; i++)
            {
                Pawn pawn = tmp[i];
                if (!SeasicknessExposure.Applies(pawn) || pawn.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Seasick))
                {
                    continue;
                }
                if (pawn.GetStatValue(DepthRimDefOf.DepthRim_SeasicknessSensitivity) <= 0f)
                {
                    continue;
                }
                Hediff_Seasick hediff = (Hediff_Seasick)HediffMaker.MakeHediff(DepthRimDefOf.DepthRim_Seasick, pawn);
                hediff.Severity = Hediff_Seasick.RestingSeverity;
                // 先結算一次（0 ticks）記下暴露量：否則剛加上時暴露量是 0，ShouldRemove 成立，原版馬上把它移除
                hediff.Advance(0);
                pawn.health.AddHediff(hediff);
            }
            tmp.Clear();
        }
    }

    /// <summary>暈船的心情：hediff 的階段 1–3（頭暈、暈船、嚴重暈船）對應想法的階段 0–2。</summary>
    public class ThoughtWorker_Seasick : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            Hediff h = p.health?.hediffSet?.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Seasick);
            if (h == null || h.CurStageIndex <= 0)
            {
                return ThoughtState.Inactive;
            }
            return ThoughtState.ActiveAtStage(System.Math.Min(h.CurStageIndex - 1, def.stages.Count - 1));
        }
    }

    /// <summary>畏光（基因設計稿 §5）：刺眼。階段 0 畏光、1 強烈畏光。</summary>
    public class ThoughtWorker_BrightLight : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            int level = DepthRimGenes.Of(p).photophobia;
            if (level <= 0 || !p.Spawned || !MarineGeneMath.Glaring(level, p.Map.glowGrid.GroundGlowAt(p.Position)))
            {
                return ThoughtState.Inactive;
            }
            return ThoughtState.ActiveAtStage(level >= 2 ? 1 : 0);
        }
    }

    /// <summary>畏光的人在黑暗中覺得安心。</summary>
    public class ThoughtWorker_ComfortingDark : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            if (DepthRimGenes.Of(p).photophobia <= 0 || !p.Spawned || !p.Awake())
            {
                return ThoughtState.Inactive;
            }
            return MarineGeneMath.Dark(p.Map.glowGrid.GroundGlowAt(p.Position)) ? ThoughtState.ActiveAtStage(0) : ThoughtState.Inactive;
        }
    }
}
