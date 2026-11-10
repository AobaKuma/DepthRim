using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace DepthRim.Acceptance
{
    /// <summary>跳幫突擊艇（設計稿 §6.2、§11 M6）。</summary>
    public static class Tests_Boarding
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>
        /// B1：冰洋測試地圖上蓋一間排乾的艙室，並開一台主動聲納。跳幫襲擊事件讓突擊艇降在艙室裡：
        /// 屋頂沒破、艙室沒進水、襲擊者穿潛水服下艇。送撤退訊號後，襲擊者回艇、艇飛走，活著的人回到世界。
        /// 另驗：沒開主動聲納就不會發生；DMSE 認得艇上的陣營（可被攔截）；摧毀停著的艇，裡面的人會掉出來。
        /// </summary>
        public static IEnumerable<Wait> B1_BoardingRaid(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            Faction enemy = Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && !f.def.hidden
                && f.def.humanlikeFaction && f.def.techLevel >= TechLevel.Industrial);
            ctx.Require(enemy != null, "an industrial enemy faction exists");
            IncidentDef incident = DefDatabase<IncidentDef>.GetNamed("DepthRim_BoardingRaid");
            // 原版在「新流浪者」後 300000 tick 內擋掉所有 ThreatBig（預設 -99999，所以開局 3.3 天內都擋）：只驗事件自己的條件
            bool Sub(IncidentParms p) => (bool)HarmonyLib.AccessTools.Method(typeof(IncidentWorker), "CanFireNowSub").Invoke(incident.Worker, new object[] { p });

            ctx.Check(Find.Storyteller.def.comps.Any(c => c is StorytellerCompProperties_BoardingRaids), $"the storyteller ({Find.Storyteller.def.defName}) schedules boarding raids");

            // 艙室
            Room room = Tests_Flooding.BuildCompartment(ctx, out CellRect inner, out List<Thing> walls);
            foreach (Wait w in Tests_Flooding.DrainCompartment(ctx, inner)) { yield return w; }

            // 沒開主動聲納：不會被跳幫
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            parms.faction = enemy;
            parms.points = 500f;
            ctx.Check(!BoardingUtility.RaidableOceanMap(map) || SonarUtility.HasActiveSonar(map, Faction.OfPlayer), "without active sonar the map is not raidable");
            bool quietBefore = !SonarUtility.HasActiveSonar(map, Faction.OfPlayer);
            if (quietBefore)
            {
                ctx.Check(!Sub(parms), "no boarding raid while the sonar is quiet");
            }

            // 主動聲納
            CellRect pad = CellRect.Empty;
            foreach (IntVec3 c in map.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 6, 4);
                if (!r.ExpandedBy(3).InBounds(map) || r.ExpandedBy(2).Overlaps(inner.ExpandedBy(2))) { continue; }
                if (r.ExpandedBy(1).Cells.All(n => n.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Medium)
                    && n.GetEdifice(map) == null && n.GetFirstItem(map) == null && n.GetFirstPawn(map) == null))
                {
                    pad = r;
                    break;
                }
            }
            ctx.Require(pad.Area > 0, "found open seabed for the sonar");
            foreach (IntVec3 c in pad) { DepthRimFixture.Spawn(ThingDefOf.PowerConduit, c, map); }
            Thing battery = DepthRimFixture.Spawn(ThingDef.Named("Battery"), pad.Min, map);
            battery.TryGetComp<CompPowerBattery>().AddEnergy(100000f);
            Thing sonar = DepthRimFixture.Spawn(ThingDef.Named("DepthRim_ActiveSonar"), pad.Min + new IntVec3(3, 0, 1), map);
            yield return new WaitUntil(() => sonar.TryGetComp<CompSearchRadar>().Active, 1200);
            ctx.Require(SonarUtility.HasActiveSonar(map, Faction.OfPlayer), "the active sonar is pinging");
            ctx.Check(BoardingUtility.RaidableOceanMap(map), "an ocean map with active sonar is raidable");
            ctx.Check(BoardingUtility.TryFindLandingCell(map, out IntVec3 found, out bool foundInside) && foundInside,
                $"the craft looks for a sealed compartment to cut into ({found}, inside {foundInside})");

            // DMSE 認得艇上的人
            Building_BoardingCraft probe = (Building_BoardingCraft)ThingMaker.MakeThing(BoardingDefOf.DepthRim_BoardingCraft);
            probe.SetFaction(enemy);
            Pawn probePawn = PawnGenerator.GeneratePawn(enemy.RandomPawnKind(), enemy);
            probe.Load(probePawn);
            ctx.Check(BVRDetection.GetDropFaction(probe) == enemy, "DMSE's interception sees who is aboard the craft");

            // 摧毀停著的艇：裡面的人掉出來
            // 在開放水域試，殘骸不會擋到後面艙室裡的降落點
            IntVec3 probeCell = DepthRimFixture.OpenWaterCell();
            probe.ejected = true;
            GenSpawn.Spawn(probe, probeCell, map);
            CellRect probeRect = probe.OccupiedRect().ExpandedBy(3).ClipInsideMap(map);
            probe.Destroy(DestroyMode.KillFinalize);
            ctx.Check(probePawn.Spawned, "destroying a parked craft spills its crew");
            if (!probePawn.Destroyed) { probePawn.Destroy(); }
            foreach (Thing leaving in probeRect.Cells.SelectMany(c => c.GetThingList(map)).Where(t => t.def.category == ThingCategory.Item || t is Filth).ToList())
            {
                leaving.Destroy();
            }

            // 跳幫襲擊：指定降在我們的艙室
            IntVec3 landing = inner.Min + new IntVec3(1, 0, 0);
            parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            parms.faction = enemy;
            parms.points = 800f;
            parms.spawnCenter = landing;
            ctx.Check(incident.TargetAllowed(map), "the boarding raid targets ocean maps");
            ctx.Require(Sub(parms), "the boarding raid can fire on this map");
            parms.forced = true;
            ctx.Require(incident.Worker.TryExecute(parms), "the boarding raid started");
            // 有主動聲納：DMSE 的地圖層攔截把突擊艇收進波次。它是水下領域；聲納遠遠聽到時只是「未知聲納接觸」
            MapComponent_BVRCombat bvr = map.GetComponent<MapComponent_BVRCombat>();
            BVRTarget held = bvr.Waves.SelectMany(w => w.targets).FirstOrDefault(t => (t.Payload as Skyfaller)?.innerContainer.FirstOrDefault() is Building_BoardingCraft);
            if (held != null)
            {
                int waveLeft = bvr.WaveOf(held).tickToImpact - Find.TickManager.TicksGame;
                ctx.Note($"craft held in a BVR wave, {waveLeft} ticks to impact, alert label '{held.DisplayLabel(waveLeft)}'");
                ctx.Check(held.domain == CombatDomain.Underwater, "the incoming craft is an underwater target");
                ctx.Check(waveLeft <= Building_BoardingCraft.ClassifyTicksBeforeImpact
                    || held.DisplayLabel(waveLeft) == "DepthRim.Sonar.UnknownContact".Translate().ToString(), "far out, the alert lists it as an unknown sonar contact");
                ctx.Check(held.DisplayLabel(0) == BoardingDefOf.DepthRim_BoardingCraft.label, "close in, it is classified as a boarding craft");
            }
            else
            {
                ctx.Note("the craft was not held in a BVR wave (no defender radar resolved)");
            }
            Building_BoardingCraft craft = null;
            yield return new WaitUntil(() =>
            {
                craft = map.listerThings.ThingsOfDef(BoardingDefOf.DepthRim_BoardingCraft).OfType<Building_BoardingCraft>().FirstOrDefault();
                return craft != null;
            }, 6000);
            ctx.Require(craft != null, "a boarding craft landed");
            ctx.Note($"craft at {craft.Position}, compartment {inner}, raiders {craft.raiders.Count}");
            ctx.Check(craft.Position.GetRoom(map) == room, "the craft cut into the compartment");
            ctx.Check(inner.Cells.All(c => c.Roofed(map)), "the compartment roof is intact");
            yield return new WaitUntil(() => craft.ejected, 600);
            ctx.Check(craft.ejected && craft.raiders.All(p => p.Spawned || p.Dead), "the boarding party came out");
            ctx.Note("boarders: " + string.Join("; ", craft.raiders.Select(p => $"{p.LabelShort} {p.kindDef.defName} [{string.Join(",", p.apparel?.WornApparel.Select(a => a.def.defName) ?? new string[0])}]")));
            ctx.Check(craft.raiders.Where(p => p.RaceProps.Humanlike).All(p => p.apparel.WornApparel.Any(a => a.def.defName == "DepthRim_DivingSuit")), "every humanlike boarder wears a diving suit (mechs need no air)");
            Lord lord = craft.raiders.Select(p => p.GetLord()).FirstOrDefault(l => l != null);
            ctx.Require(lord != null && lord.LordJob is LordJob_BoardingRaid, "the boarders run the boarding raid lord");
            yield return Updates(2);
            IntVec3 dryCell = inner.Cells.FirstOrDefault(c => !craft.OccupiedRect().Contains(c));
            // 艇降落本身不破壞密閉；之後襲擊者在艙裡開火打傷牆，牆會照常滲水，所以只要求沒有破口、水位遠低於灌滿
            ctx.Note($"compartment level {Tests_Flooding.Level(dryCell):P0}, damaged walls on map {DepthRimFixture.Flooding.DamagedWallCount}");
            ctx.Check(Tests_Flooding.Level(dryCell) < 0.25f && !DepthRimFixture.Flooding.IsOpenAt(dryCell),
                $"the compartment stays sealed after the landing ({Tests_Flooding.Level(dryCell):P0})");

            // 撤退：回艇、飛走
            List<Pawn> boarders = craft.raiders.ToList();
            Find.SignalManager.SendSignal(new Signal("DepthRim.BoardingRetreat"));
            yield return new WaitTicks(60);
            ctx.Check(lord.CurLordToil is LordToil_ReturnToCraft || !lord.ownedPawns.Any(), "the raid falls back to its craft");
            ctx.Check(craft.ReturnRequested, "the craft is waiting for its crew");
            int start = Find.TickManager.TicksGame;
            yield return new WaitUntil(() => craft.Destroyed, 7000);
            ctx.Note($"craft left after {Find.TickManager.TicksGame - start} ticks");
            ctx.Check(craft.Destroyed, "the craft left the map");
            // 倒地的人留在原地（被丟下），只算還站著的
            List<Pawn> alive = boarders.Where(p => !p.Dead && !(p.Spawned && p.Downed)).ToList();
            ctx.Note("after take-off: " + string.Join("; ", alive.Select(p => $"{p.LabelShort} spawned {p.Spawned} world {p.IsWorldPawn()} discarded {p.Discarded}")));
            ctx.Check(alive.All(p => !p.Spawned && p.MapHeld == null && (p.IsWorldPawn() || p.Discarded)), $"every boarder still standing left with it ({alive.Count} of {boarders.Count})");
            ctx.Check(inner.Cells.All(c => c.Roofed(map)), "the roof is still intact after take-off");
            yield return Updates(2);
        }
    }
}
