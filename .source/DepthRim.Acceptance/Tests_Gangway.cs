using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DepthRim.Acceptance
{
    /// <summary>可收放舷梯與開放海面上的橋（水面層）。</summary>
    public static class Tests_Gangway
    {
        /// <summary>
        /// J1：兩艘浮船隔 4 格海。舷梯放置規則；原版橋能蓋在海上；伸出後兩艘船連通、收回時等站在上面的人離開；
        /// 對岸消失時整座掉下去，上面的人爬回甲板、物品沉下去；橋被拆時站在上面的人落海；舷梯被拆時步道一起消失。
        /// </summary>
        public static IEnumerable<Wait> J1_Gangway(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            IntVec3 c = sea.Center;
            CellRect hullA = new CellRect(c.x - 8, c.z - 2, 5, 5);
            CellRect hullB = new CellRect(c.x + 1, c.z - 2, 5, 5);
            Tests_SeaSurface.LayHull(sea, hullA);
            Tests_SeaSurface.LayHull(sea, hullB);
            IntVec3 gwCell = new IntVec3(hullA.maxX, 0, c.z);
            IntVec3 farDeck = new IntVec3(hullB.minX + 2, 0, c.z);
            ThingDef gwDef = DepthRimDefOf.DepthRim_Gangway;
            TerrainDef deck = DepthRimDefOf.DepthRim_GangwayDeck;

            // ── 放置規則與原版橋 ──
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(gwDef, gwCell, Rot4.East, sea, godMode: true).Accepted, "a gangway on the deck edge facing the sea can be placed");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(gwDef, gwCell + IntVec3.West * 2, Rot4.East, sea, godMode: true).Accepted, "not in the middle of the deck (facing deck)");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(gwDef, gwCell + IntVec3.East, Rot4.East, sea, godMode: true).Accepted, "not on the open sea");
            IntVec3 bridgeSpot = new IntVec3(c.x, 0, c.z + 6);
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(TerrainDefOf.Bridge, bridgeSpot, Rot4.North, sea, godMode: true).Accepted, "a vanilla bridge can be built on open sea");
            TerrainDef heavy = DefDatabase<TerrainDef>.GetNamedSilentFail("HeavyBridge");
            if (heavy != null)
            {
                ctx.Check(GenConstruct.CanPlaceBlueprintAt(heavy, bridgeSpot, Rot4.North, sea, godMode: true).Accepted, "and a heavy bridge");
            }

            Building_Gangway gw = (Building_Gangway)ThingMaker.MakeThing(gwDef);
            gw.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(gw, gwCell, sea, Rot4.East);
            Pawn walker = DepthRimFixture.SpawnColonist(sea, hullA.CenterCell);
            walker.drafter.Drafted = true;
            ctx.Check(!walker.CanReach(farDeck, PathEndMode.OnCell, Danger.Deadly), "before extending, the other hull is out of reach");
            ctx.Check(gw.State == GangwayState.Retracted && gw.CanExtend(out _), "retracted, and there is a deck to reach");

            // ── 伸出 ──
            ctx.Check(gw.Extend(), "extend accepted");
            int per = gw.Ext.ticksPerCell;
            yield return new WaitTicks(per * 2 + 5);
            ctx.Check(gw.State == GangwayState.Extending && gw.LaidCells >= 2 && gw.LaidCells < 4, $"it lays one cell at a time ({gw.LaidCells})");
            yield return new WaitTicks(per * 4);
            ctx.Check(gw.Connected && gw.Span.Count == 4 && gw.Span.All(x => sea.terrainGrid.FoundationAt(x) == deck), $"connected across 4 cells ({gw.State}, {gw.Span.Count})");
            ctx.Check(gw.Span.All(x => sea.terrainGrid.BaseTerrainAt(x) == DepthRimDefOf.DepthRim_OpenSea), "the open sea is still underneath");
            ctx.Check(walker.CanReach(farDeck, PathEndMode.OnCell, Danger.Deadly), "the other hull can be walked to");
            Current.Game.CurrentMap = sea;
            foreach (Wait w in ctx.ScreenshotOn(sea, gwCell + IntVec3.East * 2, 7f, "gangway_extended", 30)) { yield return w; }

            // ── 收回：有人站在上面就等 ──
            walker.Position = gw.Span[1];
            walker.Notify_Teleported();
            gw.Retract();
            yield return new WaitTicks(per * 4 + 10);
            ctx.Check(gw.State == GangwayState.Retracting && gw.LaidCells == 2, $"retracting stops at the colonist standing on it ({gw.LaidCells} left)");
            ctx.Check(gw.GetInspectString().Contains(walker.LabelShort), "the inspect string says who it waits for");
            walker.Position = hullA.CenterCell;
            walker.Notify_Teleported();
            yield return new WaitTicks(per * 3 + 40);
            ctx.Check(gw.State == GangwayState.Retracted && gw.LaidCells == 0, $"once they step off it folds all the way ({gw.State})");
            ctx.Check(!sea.AllCells.Any(x => sea.terrainGrid.FoundationAt(x) == deck), "no gangway deck left on the map");
            ctx.Check(!walker.CanReach(farDeck, PathEndMode.OnCell, Danger.Deadly), "and the other hull is cut off again");

            // ── 對岸消失（船開走）：整座掉下去 ──
            gw.Extend();
            yield return new WaitTicks(per * 5 + 10);
            ctx.Require(gw.Connected, "extended again");
            walker.Position = gw.Span[3];
            walker.Notify_Teleported();
            Thing steel = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), gw.Span[0], sea);
            int climbed = SeaDrop.climbedBack;
            foreach (IntVec3 x in hullB) { sea.terrainGrid.SetTerrain(x, DepthRimDefOf.DepthRim_OpenSea); }
            yield return new WaitTicks(Building_Gangway.CheckInterval + 5);
            ctx.Check(gw.State == GangwayState.Retracted && !sea.AllCells.Any(x => sea.terrainGrid.FoundationAt(x) == deck), $"the far side gone: the gangway drops at once ({gw.State})");
            ctx.Check(walker.Spawned && !SeaSurface.IsOpenSea(walker.Position, sea) && SeaDrop.climbedBack == climbed + 1, $"the colonist on it climbs back onto a deck ({walker.Position})");
            ctx.Check(steel.Destroyed, "the steel on it sinks");
            ctx.Check(!gw.CanExtend(out _), "nothing to reach any more");

            // ── 原版橋被拆：站在上面的人落海 ──
            IntVec3 bridgeCell = new IntVec3(hullA.minX, 0, hullA.maxZ + 1);
            sea.terrainGrid.SetTerrain(bridgeCell, TerrainDefOf.Bridge);
            ctx.Check(!SeaSurface.IsOpenSea(bridgeCell, sea) && bridgeCell.Standable(sea), "a bridge over the sea can be stood on");
            walker.Position = bridgeCell;
            walker.Notify_Teleported();
            sea.terrainGrid.RemoveFoundation(bridgeCell, doLeavings: false);
            ctx.Check(walker.Spawned && walker.Position != bridgeCell && !SeaSurface.IsOpenSea(walker.Position, sea), $"removing the bridge drops them, and they climb back ({walker.Position})");

            // ── 舷梯被拆：步道一起消失 ──
            Tests_SeaSurface.LayHull(sea, hullB);
            gw.Extend();
            yield return new WaitTicks(per * 5 + 10);
            ctx.Require(gw.Connected, "extended to the hull that came back");
            List<IntVec3> span = gw.Span.ToList();
            gw.Destroy(DestroyMode.Deconstruct);
            ctx.Check(span.All(x => sea.terrainGrid.FoundationAt(x) == null && SeaSurface.IsOpenSea(x, sea)), "deconstructing the gangway takes the walkway with it");

            Tests_SeaSurface.RemoveSeaMaps(sea);
            yield return new WaitTicks(5);
        }
    }
}
