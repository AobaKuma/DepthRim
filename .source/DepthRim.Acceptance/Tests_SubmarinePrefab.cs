using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>預製潛艇（FFF 結構 <c>DepthRim_Prefab_PatrolSubmarine</c>）：生成後不用任何操作就是一艘能出航的潛艇。</summary>
    public static class Tests_SubmarinePrefab
    {
        /// <summary>
        /// S6：在冰洋地圖上挖深淵船塢、生成預製潛艇。檢查：佔地全是船體平台並蓋了屋頂；四個密閉艙室（船尾、中艙、船艏、氣閘）是乾的；
        /// 電池充滿、艦橋與推進器都通電；DMSE 認得整塊船體、兩具推進器與電池電量；船殼耐壓是變溫層（最弱的是氣閘外門）；
        /// 生成前看得見的格子沒有被 FFF 重新蓋上迷霧。最後拍一張。
        /// </summary>
        public static IEnumerable<Wait> S6_PrefabSubmarine(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return new WaitTicks(5);
            MapComponent_Flooding flooding = map.Flooding();
            ctx.Require(SubmarinePrefab.Default != null, "the prefab submarine def is loaded");

            IntVec3 exit = DepthRimFixture.Shaft.exit.Position;
            bool exitVisibleBefore = !map.fogGrid.IsFogged(exit);
            CellRect hull = DepthRimFixture.SpawnPrefabSubmarine(out IntVec3 center);
            ctx.Require(hull.Area > 0, "found room for the prefab submarine");
            ctx.Note($"prefab hull {hull} (center {center})");
            ctx.Check(hull.Width == 17 && hull.Height == 11, $"the hull is 17x11 ({hull.Width}x{hull.Height})");
            ctx.Check(!SubmarinePrefab.CanSpawnAt(map, center, Rot4.North, out string again), $"a second prefab cannot overlap the first ({again})");

            // ── 地形、屋頂、迷霧 ──
            TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            ctx.Check(hull.Cells.All(c => map.terrainGrid.TerrainAt(c) == platform), "the whole footprint is hull platform");
            ctx.Check(hull.Cells.All(c => map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed), "the whole footprint is roofed");
            ctx.Check(hull.Cells.All(c => !map.fogGrid.IsFogged(c)), "the hull is not fogged");
            ctx.Check(!exitVisibleBefore || !map.fogGrid.IsFogged(exit), "cells that were visible before stay visible (FFF only recomputes fog over the footprint)");

            // ── 建築 ──
            List<Building> buildings = hull.Cells.SelectMany(c => c.GetThingList(map)).OfType<Building>().Distinct().ToList();
            int Count(string def) => buildings.Count(b => b.def.defName == def);
            ctx.Note("buildings: " + string.Join(", ", buildings.GroupBy(b => b.def.defName).Select(g => $"{g.Key}×{g.Count()}")));
            ctx.Check(Count("DepthRim_Keel") == 1 && Count("DepthRim_Helm") == 1 && Count("DepthRim_Thruster") == 2, "a keel frame, a helm and two thrusters");
            ctx.Check(Count("DepthRim_WatertightDoor") == 4 && Count("DepthRim_BilgePump") == 4 && Count("Battery") == 3, "four watertight doors, four bilge pumps, three batteries");
            ctx.Check(Count("DepthRim_PressureHullWall") == hull.EdgeCells.Count() - 1 + 16 + 6, $"pressure walls on the edge, bulkheads and airlock ({Count("DepthRim_PressureHullWall")})");
            ctx.Check(buildings.All(b => b.Faction == Faction.OfPlayer), "everything belongs to the player");
            List<CompPowerBattery> batteries = buildings.Select(b => b.TryGetComp<CompPowerBattery>()).Where(b => b != null).ToList();
            ctx.Check(batteries.All(b => b.StoredEnergyPct > 0.99f), "the batteries start full");

            // ── 艙室：四個密閉、乾的艙室 ──
            yield return new WaitTicks(MapComponent_Flooding.UpdateInterval + 5);
            List<Room> rooms = hull.ContractedBy(1).Cells
                .Where(c => c.GetEdifice(map) == null)
                .Select(c => c.GetRoom(map)).Where(r => r != null && !r.IsDoorway).Distinct().ToList();
            ctx.Check(rooms.Count == 4, $"four rooms inside the hull ({rooms.Count})");
            ctx.Check(rooms.All(r => !flooding.IsOpenWater(r)), "every room is sealed (not open water)");
            ctx.Check(rooms.All(r => flooding.LevelOf(r) < 0.01f), $"every room is dry ({string.Join(" ", rooms.Select(r => flooding.LevelOf(r).ToString("0.00")))})");
            ctx.Check(rooms.Any(r => r.CellCount == 2), "one of them is the two-cell airlock");

            // ── 電 ──
            Building helm = buildings.First(b => b.def.defName == "DepthRim_Helm");
            List<CompPowerTrader> consumers = buildings.Select(b => b.TryGetComp<CompPowerTrader>()).Where(p => p != null).ToList();
            yield return new WaitUntil(() => consumers.All(p => p.PowerOn), 2400);
            ctx.Check(consumers.All(p => p.PowerOn), $"every powered building is on ({consumers.Count(p => p.PowerOn)}/{consumers.Count}: off = {string.Join(", ", consumers.Where(p => !p.PowerOn).Select(p => p.parent.def.defName))})");
            ctx.Check(batteries.Select(b => b.PowerNet).Distinct().Count() == 1, "the batteries share one power net");

            // ── DMSE：船體、推進器、電量 ──
            MapComponent_PileNetwork.For(map).MarkDirty();
            yield return new WaitTicks(5);
            CompTransferConsole console = helm.TryGetComp<CompTransferConsole>();
            ctx.Require(console?.Network != null, "the helm stands on the keel network");
            PileNetwork net = console.Network;
            net.RecalculateFootprint();
            ctx.Check(net.Footprint.Count == hull.Area && net.Footprint.All(hull.Contains), $"the whole platform is hull ({net.Footprint.Count}/{hull.Area})");
            net.InvalidateSurvey();
            TransferShipSurvey survey = net.Survey;
            ctx.Note($"survey: engines {survey.operationalEngines}, TWR {survey.TWR:0.##}, fuel {survey.totalFuel:0}");
            ctx.Check(survey.operationalEngines == 2, "both thrusters count as engines");
            ctx.Check(survey.totalFuel >= 1500f, $"battery charge counts as fuel ({survey.totalFuel:0})");

            // ── 船殼耐壓 ──
            DepthTier rating = HullPressure.RatingFor(map, out Building weakest);
            ctx.Note($"hull rating {rating}, weakest {weakest?.def.defName} at {weakest?.Position}");
            ctx.Check(rating == DepthTier.Thermocline, "the hull is rated to the thermocline");
            ctx.Check(weakest != null && weakest.def.defName == "DepthRim_WatertightDoor" && hull.EdgeCells.Contains(weakest.Position), "the weakest piece is the airlock's outer door");

            foreach (Wait w in ctx.ScreenshotOn(map, center, 14f, "prefab_submarine")) { yield return w; }
        }
    }
}
