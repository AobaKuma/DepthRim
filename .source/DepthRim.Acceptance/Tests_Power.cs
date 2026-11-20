using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>
    /// 洋流發電機與「水下不能用風力發電」（2026-11-19）：
    /// 3×3 的洋流發電機比照原版風力發電機（每 250 ticks 重算、通道被擋折減），
    /// 但要求整台浸在水裡；原版風力發電機在水下地圖輸出歸零、擋住放置、檢視面板說明原因。
    /// </summary>
    public static class Tests_Power
    {
        public static IEnumerable<Wait> PW1_OceanCurrentTurbine(TestContext ctx)
        {
            Map home = DepthRimFixture.Home;
            Map map = DepthRimFixture.EnsureOcean();
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("DR_OceanCurrentTurbine");
            ctx.Require(def != null, "the ocean current turbine is loaded");
            CompProperties_OceanCurrentTurbine props = def.GetCompProperties<CompProperties_OceanCurrentTurbine>();
            ctx.Require(props != null, "it has the turbine comp");
            ctx.Check(def.size == new IntVec2(3, 3), $"it is 3x3 ({def.size.x}x{def.size.z})");
            ctx.Check(def.rotatable, "it is rotatable (the current flows through its front and back)");
            ctx.Check(props.PowerConsumption == -2500f, $"it produces 2500 W ({props.PowerConsumption})");
            ctx.Check(props.minWaterLevel > 0f && props.minWaterLevel <= 1f, $"it needs to be {props.minWaterLevel:0.00} under water");
            ctx.Check(props.flowReach == OceanCurrentMath.DefaultFlowReach, $"its flow channel reaches {props.flowReach} cells");
            ctx.Check(def.researchPrerequisites != null && def.researchPrerequisites.Any(p => p.defName == "DepthRim_UnderwaterSurvival"), "it needs the starting tech");
            ctx.Check(def.placeWorkers != null && def.placeWorkers.Contains(typeof(PlaceWorker_OceanCurrentTurbine)), "it has its own placement rule");

            // 乾淨的海床：3×3 加上前後各 2 格的水流通道都要空出來
            CellRect area = DepthRimFixture.ClearOceanArea(11);
            yield return new WaitFrames(2);
            IntVec3 center = area.CenterCell;
            CellRect body = GenAdj.OccupiedRect(center, Rot4.North, def.size);
            MapComponent_Flooding flooding = map.Flooding();
            ctx.Require(flooding != null && flooding.Active, "the ice shaft map is a flooded (underwater) map");
            ctx.Check(CompOceanCurrentTurbine.IsFullySubmerged(map, flooding, body, props.minWaterLevel), "the cleared sea floor is under water");
            List<IntVec3> flowCells = OceanCurrentMath.FlowCells(center, Rot4.North, def.size, props.flowReach).ToList();
            ctx.Check(flowCells.Count == 3 * props.flowReach * 2, $"the flow channel is 3x{props.flowReach} on both sides ({flowCells.Count} cells)");
            ctx.Check(flowCells.All(c => !body.Contains(c)), "the channel does not include the building itself");

            // 放置規則：水下地圖可以，地面殖民地不行
            PlaceWorker_OceanCurrentTurbine worker = new PlaceWorker_OceanCurrentTurbine();
            AcceptanceReport onSeaFloor = worker.AllowsPlacing(def, center, Rot4.North, map);
            ctx.Check(onSeaFloor.Accepted, $"it can be placed on the sea floor ({onSeaFloor.Reason})");
            AcceptanceReport onLand = worker.AllowsPlacing(def, home.Center, Rot4.North, home);
            ctx.Check(!onLand.Accepted && onLand.Reason == "DepthRim.CurrentTurbine.MustBeUnderwater".Translate(),
                $"not on a surface map ({onLand.Reason})");
            AcceptanceReport onBlueprintAt = GenConstruct.CanPlaceBlueprintAt(def, center, Rot4.North, map, godMode: true);
            ctx.Check(onBlueprintAt.Accepted, $"the blueprint is accepted under water ({onBlueprintAt.Reason})");

            // 發電：通電要等一個重算週期
            Thing turbine = DepthRimFixture.Spawn(def, center, map, faction: Faction.OfPlayer);
            CompOceanCurrentTurbine comp = turbine.TryGetComp<CompOceanCurrentTurbine>();
            CompPowerTrader power = turbine.TryGetComp<CompPowerTrader>();
            ctx.Require(comp != null && power != null, "it spawned with its power comps");
            yield return new WaitTicks(CompOceanCurrentTurbine.UpdateInterval + 60);
            ctx.Check(comp.OnUnderwaterMap, "it knows it is on an underwater map");
            ctx.Check(comp.Submerged, "it knows the whole machine is under water");
            ctx.Check(comp.CurrentStrength > 0f, $"the current is running ({comp.CurrentStrength:0.00} of {OceanCurrentMath.MaxStrength:0.00})");
            ctx.Check(comp.BlockedCells == 0, $"the channel is clear ({comp.BlockedCells}/{comp.FlowCells})");
            float openOutput = power.PowerOutput;
            ctx.Check(openOutput > 0f, $"it feeds the grid ({openOutput:0} W)");
            float expected = comp.RatedPower * comp.CurrentStrength;
            ctx.Check(System.Math.Abs(openOutput - expected) < 1f, $"output = rated x current strength ({openOutput:0} W vs {expected:0} W)");
            ctx.Note(turbine.GetInspectString());

            // 通道被擋：牆會擋住水流，輸出按格折減
            IntVec3 wallCell = center + new IntVec3(0, 0, -2);
            DepthRimFixture.Spawn(ThingDefOf.Wall, wallCell, map, ThingDefOf.Steel, Faction.OfPlayer);
            yield return new WaitTicks(CompOceanCurrentTurbine.UpdateInterval + 60);
            ctx.Check(comp.BlockedCells >= 1, $"a wall in the channel is noticed ({comp.BlockedCells} of {comp.FlowCells} cells)");
            ctx.Check(power.PowerOutput < openOutput, $"a blocked channel cuts the output ({power.PowerOutput:0} W, was {openOutput:0} W)");
            ctx.Check(System.Math.Abs(power.PowerOutput - comp.RatedPower * comp.CurrentStrength * OceanCurrentMath.FlowFactor(comp.BlockedCells)) < 1f,
                "the reduction matches the flow factor");

            ctx.Note($"water level at the turbine: {flooding.LevelAt(center):0.00}");

            // 截圖：相機要先切到這張地圖，不然拍到的會是地面殖民地
            ctx.Check(turbine.Graphic != null && turbine.Graphic.MatSingle != null, "the turbine graphic is loaded");
            Current.Game.CurrentMap = map;
            yield return new WaitFrames(5);
            foreach (Wait w in ctx.ScreenshotOn(map, center, 12f, "ocean_current_turbine", 40)) { yield return w; }
            ctx.Note($"camera {Find.CameraDriver.MapPosition} on map {Find.CurrentMap?.uniqueID}, turbine {turbine.Position} drawPos {turbine.DrawPos}");
            foreach (Wait w in ctx.ScreenshotOn(map, center, 6f, "ocean_current_turbine_close", 30)) { yield return w; }

            // 水下不能用風力發電：放置被擋、輸出歸零、檢視面板說明
            ThingDef windDef = ThingDefOf.WindTurbine;
            ctx.Check(windDef.placeWorkers != null && windDef.placeWorkers.Contains(typeof(PlaceWorker_NoWindUnderwater)),
                "the wind turbine got the no-wind-under-water placement rule (patch xml)");
            PlaceWorker_NoWindUnderwater noWind = new PlaceWorker_NoWindUnderwater();
            ctx.Check(!noWind.AllowsPlacing(windDef, home.Center, Rot4.North, map).Accepted, "a wind turbine cannot be placed under water");
            ctx.Check(noWind.AllowsPlacing(windDef, home.Center, Rot4.North, home).Accepted, "and is still fine on the surface");

            CellRect windRect = GenAdj.OccupiedRect(new IntVec3(area.minX, 0, area.minZ), Rot4.North, windDef.size);
            foreach (IntVec3 c in windRect)
            {
                DepthRimFixture.ClearCell(map, c, TerrainDefOf.Gravel);
                map.roofGrid.SetRoof(c, null);
            }
            yield return new WaitFrames(2);
            Thing wind = DepthRimFixture.Spawn(windDef, windRect.CenterCell, map, faction: Faction.OfPlayer);
            CompPowerPlantWind windComp = wind.TryGetComp<CompPowerPlantWind>();
            CompPowerTrader windPower = wind.TryGetComp<CompPowerTrader>();
            ctx.Require(windComp != null && windPower != null, "the wind turbine spawned with its comps");
            yield return new WaitTicks(120);
            ctx.Check(windPower.PowerOutput == 0f, $"under water the wind turbine produces nothing ({windPower.PowerOutput:0} W)");
            string windInspect = wind.GetInspectString();
            ctx.Check(windInspect.Contains("DepthRim.WindTurbine.NoWindUnderwaterInspect".Translate()), "the inspect pane says why");
            ctx.Note(windInspect);

            // 地面殖民地：不是水下地圖，洋流發電機不發電
            IntVec3 dry = home.Center + new IntVec3(10, 0, 10);
            dry.x = UnityEngine.Mathf.Clamp(dry.x, 5, home.Size.x - 6);
            dry.z = UnityEngine.Mathf.Clamp(dry.z, 5, home.Size.z - 6);
            foreach (IntVec3 c in GenAdj.OccupiedRect(dry, Rot4.North, def.size).ExpandedBy(1))
            {
                DepthRimFixture.ClearCell(home, c, TerrainDefOf.Concrete);
            }
            yield return new WaitFrames(2);
            Thing dryTurbine = DepthRimFixture.Spawn(def, dry, home, faction: Faction.OfPlayer);
            CompOceanCurrentTurbine dryComp = dryTurbine.TryGetComp<CompOceanCurrentTurbine>();
            CompPowerTrader dryPower = dryTurbine.TryGetComp<CompPowerTrader>();
            ctx.Require(dryComp != null && dryPower != null, "the turbine spawned on the surface too");
            yield return new WaitTicks(CompOceanCurrentTurbine.UpdateInterval + 60);
            ctx.Check(!dryComp.OnUnderwaterMap, "on the surface it knows it is not under water");
            ctx.Check(!dryComp.Submerged, "and that the machine is not submerged");
            ctx.Check(dryPower.PowerOutput == 0f, $"so it produces nothing ({dryPower.PowerOutput:0} W)");
            ctx.Note(dryTurbine.GetInspectString());
            ctx.Check(def.drawerType == DrawerType.MapMeshAndRealTime, $"it is drawn every frame ({def.drawerType})");
            // 水下太暗看不到本體，所以在有日照的地面地圖上也拍一張，確認貼圖與朝向正常
            foreach (Wait w in ctx.ScreenshotOn(home, dry, 10f, "ocean_current_turbine_surface", 40)) { yield return w; }

            Current.Game.CurrentMap = home;
            yield return new WaitFrames(5);
        }
    }
}
