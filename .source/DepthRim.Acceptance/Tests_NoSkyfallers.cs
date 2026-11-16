using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>水下地圖沒有天空：原版會掉 skyfaller／空投艙的事件、空投到達方式與圍攻都被擋下。</summary>
    public static class Tests_NoSkyfallers
    {
        private static readonly string[] Blocked =
        {
            "ShipChunkDrop", "MeteoriteImpact", "ResourcePodCrash", "DefoliatorShipPartCrash", "PsychicEmanatorShipPartCrash",
            "MechCluster", "OrbitalDebris", "VolcanicDebris", "WarpedObelisk_Abductor", "WarpedObelisk_Duplicator", "WarpedObelisk_Mutator",
            "RefugeePodCrash", "RefugeePodCrash_Baby", "RefugeePodCrash_Ghoul",
            "MysteriousCargoCube", "MysteriousCargoRevenantSpine", "MysteriousCargoUnnaturalCorpse",
        };

        /// <summary>
        /// K1：每個會掉東西的原版事件在海洋地圖上連 forced 都不能觸發、直接執行也失敗且不生成 skyfaller；
        /// 地表殖民地照常可以；空投到達方式與圍攻在水下不能用；DepthRim 自己的事件不受影響。
        /// </summary>
        public static IEnumerable<Wait> K1_NoSkyfallersUnderwater(TestContext ctx)
        {
            Map ocean = DepthRimFixture.EnsureOcean();
            Map home = DepthRimFixture.Home;
            ctx.Require(ocean != null && home != null && ocean != home, "have an ocean map and a surface home");
            yield return new WaitFrames(1);

            List<IncidentDef> defs = Blocked.Select(n => DefDatabase<IncidentDef>.GetNamedSilentFail(n)).Where(d => d != null).ToList();
            ctx.Note($"{defs.Count}/{Blocked.Length} vanilla skyfaller incidents loaded (DLC-dependent)");
            ctx.Require(defs.Count >= 3, "at least the core skyfaller incidents exist");

            foreach (IncidentDef def in defs)
            {
                ctx.Check(UnderwaterSkyfallerBlock.IsSkyfallerIncident(def), $"{def.defName} is classified as a skyfaller incident");
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, ocean);
                parms.forced = true;
                ctx.Check(!def.Worker.CanFireNow(parms), $"{def.defName} cannot fire on the ocean map, even forced");
            }

            // 地表照常：核心三個事件在殖民地上 forced 可以觸發
            foreach (string name in new[] { "ShipChunkDrop", "MeteoriteImpact", "ResourcePodCrash" })
            {
                IncidentDef def = DefDatabase<IncidentDef>.GetNamed(name);
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, home);
                parms.forced = true;
                ctx.Check(def.Worker.CanFireNow(parms), $"{name} still fires on the surface home");
            }

            // 直接 TryExecute（略過 CanFireNow）也不會生成任何 skyfaller
            int fallersBefore = ocean.listerThings.AllThings.Count(t => t is Skyfaller);
            foreach (string name in new[] { "ShipChunkDrop", "MeteoriteImpact", "ResourcePodCrash" })
            {
                IncidentDef def = DefDatabase<IncidentDef>.GetNamed(name);
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, ocean);
                parms.forced = true;
                ctx.Check(!def.Worker.TryExecute(parms), $"{name}.TryExecute fails on the ocean map");
            }
            yield return new WaitFrames(1);
            ctx.Check(ocean.listerThings.AllThings.Count(t => t is Skyfaller) == fallersBefore, "no skyfaller was spawned on the ocean map");

            // 空投到達方式與圍攻
            Faction pirates = Find.FactionManager.RandomEnemyFaction(allowNonHumanlike: false);
            ctx.Require(pirates != null, "an enemy faction exists");
            IncidentParms raid = new IncidentParms { target = ocean, faction = pirates, points = 500f };
            foreach (PawnsArrivalModeDef mode in DefDatabase<PawnsArrivalModeDef>.AllDefs.Where(UnderwaterSkyfallerBlock.IsDropArrival))
            {
                ctx.Check(!mode.Worker.CanUseWith(raid), $"arrival mode {mode.defName} is unusable underwater");
            }
            ctx.Check(UnderwaterSkyfallerBlock.IsDropArrival(PawnsArrivalModeDefOf.EdgeDrop) && UnderwaterSkyfallerBlock.IsDropArrival(PawnsArrivalModeDefOf.CenterDrop), "edge and center drop are drop arrivals");
            PawnsArrivalModeDef boarding = DefDatabase<PawnsArrivalModeDef>.GetNamed("DepthRim_BoardingArrival");
            ctx.Check(!UnderwaterSkyfallerBlock.IsDropArrival(boarding), "DepthRim's boarding craft arrival is not blocked");
            ctx.Check(!DefDatabase<RaidStrategyDef>.GetNamed("Siege").Worker.CanUseWith(raid, PawnGroupKindDefOf.Combat), "siege (drop-pod supplies) is unusable underwater");

            // 預先指定空投的襲擊：清掉指定後重新挑，挑不到就失敗，但絕不空投
            IncidentParms preset = new IncidentParms { target = ocean, faction = pirates, points = 500f, forced = true, raidArrivalMode = PawnsArrivalModeDefOf.CenterDrop, raidStrategy = RaidStrategyDefOf.ImmediateAttack };
            ((IncidentWorker_Raid)IncidentDefOf.RaidEnemy.Worker).TryGenerateRaidInfo(preset, out _, debugTest: true);
            ctx.Check(!UnderwaterSkyfallerBlock.IsDropArrival(preset.raidArrivalMode), $"a preset drop arrival is not used underwater ({preset.raidArrivalMode?.defName ?? "none"})");

            ctx.Check(!UnderwaterSkyfallerBlock.IsSkyfallerIncident(DefDatabase<IncidentDef>.GetNamed("DepthRim_BoardingRaid")), "DepthRim's boarding raid is not blocked");
        }
    }
}
