using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DepthRim.Acceptance
{
    /// <summary>跳幫 6.7：俘虜與戰利品、沉沒、艙外破牆、玩家的突擊艇。</summary>
    public static class Tests_Boarding2
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>
        /// B2：
        /// 一、敵方艇帶著一名倒地的殖民者與一件值錢的東西離開：殖民者成為該陣營的俘虜，東西沒了。
        /// 二、被擊落的突擊艇沉沒：艇上的人陣亡，遺體與艇身碎片沉在附近的海床，不走 DMSE 的殘骸空投。
        /// 三、降在艙外的跳幫用原版的破牆突擊；降在艙室裡的直接突擊。
        /// 四、玩家的突擊艇：只能造在月池上；殖民者登艇、出發到海路 2～3 格外的海洋格，抵達後開出新地圖、降落、放人。
        /// </summary>
        public static IEnumerable<Wait> B2_BoardingFollowUps(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            Faction enemy = Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && !f.def.hidden
                && f.def.humanlikeFaction && f.def.techLevel >= TechLevel.Industrial);
            ctx.Require(enemy != null, "an industrial enemy faction exists");
            Pawn MakeRaider()
            {
                PawnKindDef kind = enemy.def.pawnGroupMakers.SelectMany(g => g.options).Select(o => o.kind).FirstOrDefault(k => k.RaceProps.Humanlike)
                    ?? enemy.RandomPawnKind();
                return PawnGenerator.GeneratePawn(kind, enemy);
            }
            IntVec3 open = DepthRimFixture.OpenWaterCell();
            ctx.Require(open.IsValid, "found an open-water cell");

            // 一、俘虜與戰利品
            Building_BoardingCraft raidCraft = (Building_BoardingCraft)ThingMaker.MakeThing(BoardingDefOf.DepthRim_BoardingCraft);
            raidCraft.SetFaction(enemy);
            Pawn raider = MakeRaider();
            raidCraft.Load(raider);
            Pawn captive = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            ((IThingHolder)raidCraft).GetDirectlyHeldThings().TryAdd(captive);
            Thing loot = ThingMaker.MakeThing(ThingDefOf.Silver);
            loot.stackCount = 500;
            ((IThingHolder)raidCraft).GetDirectlyHeldThings().TryAdd(loot);
            raidCraft.SendAboardAway();
            ctx.Check(captive.IsKidnapped() && enemy.kidnapped.KidnappedPawnsListForReading.Contains(captive), "a colonist carried aboard becomes the boarders' captive");
            ctx.Check(loot.Destroyed, "loot carried aboard is gone with them");
            ctx.Check(raider.IsWorldPawn(), "the boarder leaves as a world pawn");
            raidCraft.Destroy();

            // 二、沉沒
            Building_BoardingCraft doomed = (Building_BoardingCraft)ThingMaker.MakeThing(BoardingDefOf.DepthRim_BoardingCraft);
            doomed.SetFaction(enemy);
            Pawn crewman = MakeRaider();
            doomed.Load(crewman);
            Skyfaller faller = SkyfallerMaker.MakeSkyfaller(BoardingDefOf.DepthRim_BoardingCraftIncoming, doomed);
            BVRTarget target = new BVRTarget(faller, open, map, 9001);
            int slagBefore = map.listerThings.ThingsOfDef(ThingDefOf.ChunkSlagSteel).Count;
            target.SpawnDebris(map, open);
            ctx.Check(crewman.Dead, "the crew of a sunk craft is lost");
            ctx.Check(crewman.Corpse != null && crewman.Corpse.Spawned && crewman.Corpse.Position.DistanceTo(open) <= 12f, $"the body sank to the seabed nearby ({crewman.Corpse?.Position})");
            ctx.Check(map.listerThings.ThingsOfDef(ThingDefOf.ChunkSlagSteel).Count >= slagBefore + 2, "wreckage of the craft lies on the seabed");
            ctx.Check(!map.listerThings.AllThings.Any(t => t is ActiveTransporter || (t is Skyfaller s && s.def.defName.StartsWith("DropPod"))), "no DMSE wreckage drop pod is used underwater");
            ctx.Check(doomed.Destroyed, "the sunk craft is gone");

            // 三、破牆
            ctx.Check(!BoardingUtility.IsInsideCompartment(map, open), "open water is outside any compartment");
            StateGraph breachGraph = new LordJob_BoardingRaid(enemy, breach: true).CreateGraph();
            StateGraph directGraph = new LordJob_BoardingRaid(enemy, breach: false).CreateGraph();
            ctx.Check(breachGraph.StartingToil is LordToil_AssaultColonyBreaching, "boarders landing outside start by breaching the hull");
            ctx.Check(directGraph.StartingToil is LordToil_AssaultColony, "boarders inside a compartment attack straight away");
            ctx.Check(breachGraph.transitions.Count(t => t.target is LordToil_ReturnToCraft && t.sources.Count == 2) >= 4, "breachers can still fall back to their craft");

            // 四、玩家的突擊艇
            CellRect pad = CellRect.Empty;
            foreach (IntVec3 c in map.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 5, 4);
                if (!r.ExpandedBy(3).InBounds(map)) { continue; }
                if (r.ExpandedBy(1).Cells.All(n => n.Standable(map) && n.GetEdifice(map) == null && n.GetFirstItem(map) == null
                    && n.GetFirstPawn(map) == null && n.GetFirstBuilding(map) == null && !n.Roofed(map)))
                {
                    pad = r;
                    break;
                }
            }
            ctx.Require(pad.Area > 0, "found open seabed for a moon pool");
            foreach (IntVec3 c in pad.ExpandedBy(2).ClipInsideMap(map)) { map.fogGrid.Unfog(c); }
            ThingDef playerCraftDef = BoardingDefOf.DepthRim_PlayerBoardingCraft;
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(playerCraftDef, pad.CenterCell, Rot4.North, map).Accepted, "the craft cannot be placed off a moon pool");
            Thing moonPool = DepthRimFixture.Spawn(BoardingDefOf.DepthRim_MoonPool, pad.CenterCell, map);
            CellRect poolRect = moonPool.OccupiedRect();
            ctx.Check(poolRect.Width == 5 && poolRect.Height == 4, $"the moon pool covers 5x4 ({poolRect})");
            IntVec3 craftCell = moonPool.Position;
            AcceptanceReport onPool = GenConstruct.CanPlaceBlueprintAt(playerCraftDef, craftCell, Rot4.North, map);
            ctx.Check(onPool.Accepted, $"the craft can be placed on the moon pool ({onPool.Reason})");
            Building_BoardingCraft craft = (Building_BoardingCraft)DepthRimFixture.Spawn(playerCraftDef, craftCell, map);
            ctx.Check(!moonPool.Destroyed, "building the craft keeps the moon pool");
            yield return new WaitTicks(Building_BoardingCraft.EjectDelayTicks + 40);
            ctx.Check(craft.Faction == Faction.OfPlayer && craft.ejected, "a new player craft is empty and ready");
            Command launchGizmo = craft.GetGizmos().OfType<Command_Action>().FirstOrDefault(g => g.defaultLabel == "DepthRim.Boarding.Launch".Translate());
            ctx.Check(launchGizmo != null && launchGizmo.Disabled, "launch is disabled with nobody aboard");

            Pawn colonist = DepthRimFixture.SpawnColonist(map, poolRect.Min + new IntVec3(0, 0, -1));
            Job board = JobMaker.MakeJob(BoardingDefOf.DepthRim_BoardCraft, craft);
            board.playerForced = true;
            colonist.jobs.TryTakeOrderedJob(board, JobTag.Misc);
            yield return new WaitUntil(() => !colonist.Spawned, 1200);
            ctx.Require(craft.Aboard.Contains(colonist), "the colonist boarded the craft");
            ctx.Check(craft.GetGizmos().OfType<Command_Action>().Any(g => g.defaultLabel == "DepthRim.Boarding.Launch".Translate() && !g.Disabled), "launch is available with a crew aboard");

            Dictionary<PlanetTile, BiomeDef> biomeBefore = Tests_TorpedoDefense.OceanRings(map.Tile, 3);
            MapParent arrivedAt = null;
            try
            {
                PlanetTile dest = PlanetTile.Invalid;
                foreach (PlanetTile t in biomeBefore.Keys)
                {
                    int d = OceanRouting.Distance(map.Tile, t);
                    if (d >= 2 && d <= 3 && !Find.WorldObjects.AnyWorldObjectAt(t)) { dest = t; break; }
                }
                ctx.Require(dest.Valid, "found an empty ocean tile 2-3 tiles away by sea");
                ctx.Check(Building_BoardingCraft.DestinationBlockReason(map.Tile, dest) == null, "the destination is within range by sea");
                ctx.Check(Building_BoardingCraft.DestinationBlockReason(map.Tile, map.Tile) != null, "the craft cannot launch at its own tile");
                craft.Launch(dest);
                WorldObject_TravelingBoardingCraft traveling = null;
                yield return new WaitUntil(() =>
                {
                    traveling = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_TravelingBoardingCraft>().FirstOrDefault();
                    return traveling != null;
                }, 1200);
                ctx.Require(traveling != null, "the craft left the map and is running through the water");
                ctx.Check(traveling.Craft == craft && craft.Aboard.Contains(colonist), "the crew is aboard the traveling craft");
                int arriveIn = traveling.arriveTick - Find.TickManager.TicksGame;
                ctx.Note($"traveling to {dest}, {OceanRouting.Distance(map.Tile, dest)} tiles, arrives in {arriveIn} ticks");
                yield return new WaitUntil(() => traveling.Destroyed, arriveIn + 600);
                ctx.Require(traveling.Destroyed, "the craft reached its destination");
                arrivedAt = Find.WorldObjects.MapParentAt(dest);
                ctx.Require(arrivedAt != null && arrivedAt.HasMap, "an ocean site with a map was opened at the destination");
                Map destMap = arrivedAt.Map;
                yield return new WaitUntil(() => craft.Spawned && craft.Map == destMap && craft.ejected, 1500);
                ctx.Check(craft.Spawned && craft.Map == destMap, "the craft landed on the destination map");
                ctx.Check(colonist.Spawned && colonist.Map == destMap, "the crew came out at the destination");
            }
            finally
            {
                if (colonist.Spawned) { colonist.DeSpawn(); }
                if (!colonist.Destroyed) { colonist.Destroy(); }
                if (arrivedAt != null && arrivedAt.HasMap)
                {
                    Current.Game.CurrentMap = map;
                    Current.Game.DeinitAndRemoveMap(arrivedAt.Map, false);
                }
                if (arrivedAt != null && !arrivedAt.Destroyed) { arrivedAt.Destroy(); }
                foreach (WorldObject_TravelingBoardingCraft t in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_TravelingBoardingCraft>().ToList()) { t.Destroy(); }
                foreach (KeyValuePair<PlanetTile, BiomeDef> kv in biomeBefore) { kv.Key.Tile.PrimaryBiome = kv.Value; }
                OceanRouting.ClearCache();
            }
            yield return Updates(1);
        }
    }
}
