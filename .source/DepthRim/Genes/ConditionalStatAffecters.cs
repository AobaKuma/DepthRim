using RimWorld;
using Verse;

namespace DepthRim
{
    // 基因設計稿 §6.0 B：寫一次，之後的基因只要在 conditionalStatAffecters 裡用。

    /// <summary>沒入水中（涉水嚴重度 = 水位 ≥ 0.75）。</summary>
    public class ConditionalStatAffecter_Submerged : ConditionalStatAffecter
    {
        public const float Level = 0.75f;

        public override string Label => "DepthRim.StatsReport.Submerged".Translate();

        public override bool Applies(StatRequest req)
        {
            if (!(req.Thing is Pawn pawn) || !pawn.Spawned || pawn.health == null)
            {
                return false;
            }
            Hediff wading = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Wading);
            return wading != null && wading.Severity >= Level;
        }
    }

    /// <summary>在水下地圖上（冰洋地圖、航行中的潛艇；乾艙也算）。</summary>
    public class ConditionalStatAffecter_UnderwaterMap : ConditionalStatAffecter
    {
        public override string Label => "DepthRim.StatsReport.UnderwaterMap".Translate();

        public override bool Applies(StatRequest req)
        {
            return req.HasThing && IsUnderwater(req.Thing.MapHeld);
        }

        public static bool IsUnderwater(Map map)
        {
            return map != null && (FloodingUtility.IsWaterMap(map) || VoyageView.IsVoyageMap(map));
        }
    }

    /// <summary>不在水下地圖上（在地圖上才算：商隊、運輸途中不算，免得旅行時一直有減益）。</summary>
    public class ConditionalStatAffecter_NotUnderwaterMap : ConditionalStatAffecter
    {
        public override string Label => "DepthRim.StatsReport.NotUnderwaterMap".Translate();

        public override bool Applies(StatRequest req)
        {
            Map map = req.HasThing ? req.Thing.MapHeld : null;
            return map != null && !ConditionalStatAffecter_UnderwaterMap.IsUnderwater(map);
        }
    }

    /// <summary>在海面地圖的水面層或平台層。</summary>
    public class ConditionalStatAffecter_OnSeaSurface : ConditionalStatAffecter
    {
        public override string Label => "DepthRim.StatsReport.OnSeaSurface".Translate();

        public override bool Applies(StatRequest req)
        {
            return req.HasThing && SeaLevels.IsAnyLevel(req.Thing.MapHeld);
        }
    }

    /// <summary>所在格光照 ≥ <see cref="minGlow"/>（原版人造光最多 0.5，日光與過曝範圍到 1）。</summary>
    public class ConditionalStatAffecter_BrightLight : ConditionalStatAffecter
    {
        public float minGlow = MarineGeneMath.PhotophobiaGlow;

        public override string Label => "DepthRim.StatsReport.BrightLight".Translate();

        public override bool Applies(StatRequest req)
        {
            if (!req.HasThing || !req.Thing.Spawned)
            {
                return false;
            }
            return req.Thing.Map.glowGrid.GroundGlowAt(req.Thing.Position) >= minGlow;
        }
    }

    /// <summary>所在格光照低於 <see cref="maxGlow"/>。</summary>
    public class ConditionalStatAffecter_Dark : ConditionalStatAffecter
    {
        public float maxGlow = MarineGeneMath.DarkGlow;

        public override string Label => "DepthRim.StatsReport.Dark".Translate();

        public override bool Applies(StatRequest req)
        {
            if (!req.HasThing || !req.Thing.Spawned)
            {
                return false;
            }
            return req.Thing.Map.glowGrid.GroundGlowAt(req.Thing.Position) < maxGlow;
        }
    }
}
