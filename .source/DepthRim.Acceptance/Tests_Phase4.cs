using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>
    /// 使用者回報（2026-11-01 第三輪）：艙室水面兩側的牆縫、露天房間要慢慢進水、進水效果跟牆體耐久階段相關。
    /// </summary>
    public static class Tests_Phase4
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        private static void SetRoomLevel(Map map, Room room, float level)
        {
            byte[] arr = HarmonyLib.Traverse.Create(DepthRimFixture.Flooding).Field("levels").GetValue<byte[]>();
            byte b = FloodingMath.ToByte(level);
            foreach (IntVec3 c in room.Cells) { arr[map.cellIndices.CellToIndex(c)] = b; }
        }

        /// <summary>
        /// V18：
        /// <list type="number">
        /// <item>牆縫：艙室水面往東西牆格延伸半格（填滿牆貼圖兩側的透明邊）。</item>
        /// <item>屋頂破洞：排乾的艙室拿掉一格屋頂，水從破洞慢慢灌進來（不是一瞬間滿），破洞是進水效果點，灌滿才算開放水域。</item>
        /// <item>進水階段：同一面牆打到 80% / 60% / 30% / 10%，進水點的階段 0→3、進水聲跟著換、噴出的水花越來越多。</item>
        /// </list>
        /// </summary>
        public static IEnumerable<Wait> V18_GapsRoofFillAndLeakStages(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            CellRect ring = DepthRimFixture.ClearOceanArea(7);
            List<Thing> walls = DepthRimFixture.BuildWallRing(ring);
            CellRect inner = ring.ContractedBy(1);
            Room room = inner.CenterCell.GetRoom(map);
            ctx.Require(room != null && !flooding.IsOpenWater(room), "sealed compartment");
            yield return Updates(1);
            SetRoomLevel(map, room, 0.5f);
            yield return Updates(1);

            // 1. 牆縫
            IntVec3 eastEdge = new IntVec3(inner.maxX, 0, inner.CenterCell.z);
            SectionLayer vol = map.mapDrawer.SectionAt(eastEdge).GetLayer(typeof(SectionLayer_WaterVolume));
            vol.Regenerate();
            LayerSubMesh sm = vol.subMeshes.FirstOrDefault(m => m.verts.Count > 0);
            bool eastStrip = false, westStrip = false;
            IntVec3 westEdge = new IntVec3(inner.minX, 0, inner.CenterCell.z);
            SectionLayer volW = map.mapDrawer.SectionAt(westEdge).GetLayer(typeof(SectionLayer_WaterVolume));
            volW.Regenerate();
            LayerSubMesh smW = volW.subMeshes.FirstOrDefault(m => m.verts.Count > 0);
            for (int i = 0; sm != null && i + 3 < sm.verts.Count; i += 4)
            {
                if (Mathf.Abs(sm.verts[i].x - (eastEdge.x + 1)) < 0.001f && Mathf.Abs(sm.verts[i + 3].x - (eastEdge.x + 1 + SectionLayer_WaterVolume.WallOverlap)) < 0.001f
                    && Mathf.Abs(sm.verts[i].z - eastEdge.z) < 0.001f) { eastStrip = true; }
            }
            for (int i = 0; smW != null && i + 3 < smW.verts.Count; i += 4)
            {
                if (Mathf.Abs(smW.verts[i].x - (westEdge.x - SectionLayer_WaterVolume.WallOverlap)) < 0.001f && Mathf.Abs(smW.verts[i + 2].x - westEdge.x) < 0.001f
                    && Mathf.Abs(smW.verts[i].z - westEdge.z) < 0.001f) { westStrip = true; }
            }
            ctx.Check(eastStrip, "the water reaches into the east wall's transparent margin");
            ctx.Check(westStrip, "the water reaches into the west wall's transparent margin");
            // 1b. 室內建築依 fillPercent 淹沒：排水泵（0.3）在半淹時整個泡在水裡，木柴發電機（0.9）露出水面
            IntVec3 pumpCell = inner.CenterCell + IntVec3.West * 2;
            Thing pump = DepthRimFixture.Spawn(DepthRimFixture.BilgePump, pumpCell, map);
            // 測試開了無限電力，泵會真的把水抽掉；這裡只要它的高度，所以先關掉
            CompFlickable flick = pump.TryGetComp<CompFlickable>();
            flick.SwitchIsOn = false;
            IntVec3 genCell = inner.CenterCell + IntVec3.East;
            Thing gen = DepthRimFixture.Spawn(ThingDef.Named("WoodFiredGenerator"), genCell, map);
            SetRoomLevel(map, inner.CenterCell.GetRoom(map), 0.5f);
            yield return Updates(1);
            float lvl = flooding.LevelAt(pumpCell);
            ctx.Check(SectionLayer_WaterVolume.SubmergedBuildingAt(pumpCell, lvl, map), $"the bilge pump (fill {pump.def.fillPercent:0.00}) is under water at {lvl:P0}");
            ctx.Check(gen.OccupiedRect().Cells.All(c => !SectionLayer_WaterVolume.SubmergedBuildingAt(c, lvl, map)), $"the generator (fill {gen.def.fillPercent:0.00}) sticks out of the water at {lvl:P0}");
            SectionLayer pumpLayer = map.mapDrawer.SectionAt(pumpCell).GetLayer(typeof(SectionLayer_WaterVolume));
            pumpLayer.Regenerate();
            LayerSubMesh pm = pumpLayer.subMeshes.FirstOrDefault(m => m.verts.Count > 0);
            float overlayY = AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.005f;
            bool pumpOverlay = false, genOverlay = false;
            for (int i = 0; pm != null && i + 3 < pm.verts.Count; i += 4)
            {
                if (Mathf.Abs(pm.verts[i].y - overlayY) > 0.001f) { continue; }
                IntVec3 at = new IntVec3(Mathf.RoundToInt(pm.verts[i].x), 0, Mathf.RoundToInt(pm.verts[i].z));
                if (at == pumpCell) { pumpOverlay = true; }
                if (gen.OccupiedRect().Contains(at)) { genOverlay = true; }
            }
            ctx.Check(pumpOverlay, "a water overlay is drawn above the submerged pump");
            ctx.Check(!genOverlay, "no overlay above the generator that sticks out");

            foreach (Wait w in ctx.ScreenshotOn(map, inner.CenterCell, 9f, "wall_gaps_filled", 30)) { yield return w; }
            yield return new WaitFrames(20);
            pump.Destroy(DestroyMode.Vanish);
            gen.Destroy(DestroyMode.Vanish);

            // 2. 屋頂破洞：慢慢進水
            SetRoomLevel(map, room, 0f);
            yield return Updates(1);
            SetRoomLevel(map, room, 0f);
            IntVec3 hole = inner.CenterCell + IntVec3.North;
            map.roofGrid.SetRoof(hole, null);
            yield return Updates(1);
            Room open = inner.CenterCell.GetRoom(map);
            ctx.Check(flooding.IsOpenWater(open) && !open.TouchesMapEdge, "a roof hole connects the compartment to the sea above");
            float first = flooding.LevelAt(inner.CenterCell);
            ctx.Check(first > 0f && first < 0.5f, $"the water pours in through the roof gradually ({first:P0} after one update)");
            ctx.Check(!flooding.IsOpenAt(inner.CenterCell), "a filling cell is not open water yet");
            int holeIndex = flooding.InflowCells.ToList().IndexOf(hole);
            ctx.Check(holeIndex >= 0, "the roof hole is an inflow point");
            yield return Updates(2);
            float later = flooding.LevelAt(inner.CenterCell);
            ctx.Check(later > first, $"the level keeps rising ({first:P0} -> {later:P0})");
            foreach (Wait w in ctx.ScreenshotOn(map, inner.CenterCell, 9f, "roof_hole_filling", 30)) { yield return w; }
            yield return new WaitFrames(20);
            map.roofGrid.SetRoof(hole, RoofDefOf.RoofConstructed);
            yield return Updates(1);
            ctx.Check(!flooding.IsOpenWater(inner.CenterCell.GetRoom(map)), "re-roofing seals the compartment again");

            // 3. 進水階段
            Thing wall = walls.First(w => w.Position.z == ring.minZ && w.Position.x == inner.CenterCell.x);
            Current.Game.CurrentMap = map;
            Find.CameraDriver.JumpToCurrentMapLoc(wall.Position);
            int previousEffects = -1;
            float[] fractions = { 0.8f, 0.6f, 0.3f, 0.1f };
            for (int stage = 0; stage < 4; stage++)
            {
                SetRoomLevel(map, room, 0f);
                wall.HitPoints = Mathf.Max(1, Mathf.RoundToInt(wall.MaxHitPoints * fractions[stage]));
                yield return Updates(1);
                SetRoomLevel(map, room, 0f);
                int idx = flooding.InflowCells.ToList().IndexOf(wall.Position);
                ctx.Check(idx >= 0 && flooding.InflowStages[idx] == stage, $"wall at {fractions[stage]:P0}: leak stage {stage} ({(idx >= 0 ? flooding.InflowStages[idx] : -1)})");
                ctx.Check(flooding.Audio.FloodLoopStageAt(wall.Position) == stage, $"wall at {fractions[stage]:P0}: the flood sound uses stage {stage}");
                int before = flooding.LeakEffectsSpawned;
                yield return new WaitTicks(MapComponent_Flooding.LeakEffectInterval * 8);
                int spawned = flooding.LeakEffectsSpawned - before;
                ctx.Note($"stage {stage}: {spawned} spray burst(s) in {MapComponent_Flooding.LeakEffectInterval * 8} ticks");
                if (stage > 0) { ctx.Check(spawned >= previousEffects, $"stage {stage} sprays at least as often as stage {stage - 1}"); }
                previousEffects = spawned;
            }
            foreach (Wait w in ctx.ScreenshotOn(map, wall.Position, 7f, "leak_stage_3", 30)) { yield return w; }
            yield return new WaitFrames(20);
        }

        /// <summary>
        /// V19：左下角的水位行（和真空同一個位置）與右側天氣顯示「海底」。
        /// 實際的滑鼠位置在自動測試裡無法控制，所以檢查文字來源（<see cref="Patch_MouseoverReadout.LineFor"/>）與 transpiler 有掛上。
        /// </summary>
        public static IEnumerable<Wait> V19_ReadoutAndWeather(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            yield return Updates(1);
            HarmonyLib.Patches patches = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(MouseoverReadout), nameof(MouseoverReadout.MouseoverReadoutOnGUI)));
            ctx.Check(patches != null && patches.Transpilers.Any(p => p.PatchMethod.DeclaringType == typeof(Patch_MouseoverReadout)), "the mouseover readout transpiler is applied");

            IntVec3 open = DepthRimFixture.OpenWaterCell();
            ctx.Require(open.IsValid, "found an open-water cell");
            string openLine = Patch_MouseoverReadout.LineFor(map, open);
            ctx.Check(openLine != null && openLine.Contains("100%"), $"open water reads full ({openLine})");
            ctx.Check(openLine == "DepthRim.Readout.WaterLevelOpen".Translate("100%").ToString(), "open water uses the open-water label");

            CellRect ring = DepthRimFixture.ClearOceanArea(5);
            DepthRimFixture.BuildWallRing(ring);
            Room room = ring.CenterCell.GetRoom(map);
            byte[] arr = HarmonyLib.Traverse.Create(flooding).Field("levels").GetValue<byte[]>();
            foreach (IntVec3 c in room.Cells) { arr[map.cellIndices.CellToIndex(c)] = FloodingMath.ToByte(0.4f); }
            yield return Updates(1);
            string roomLine = Patch_MouseoverReadout.LineFor(map, ring.CenterCell);
            ctx.Check(roomLine == "DepthRim.Readout.WaterLevel".Translate(flooding.LevelAt(ring.CenterCell).ToStringPercent("0")).ToString(), $"a compartment reads its level ({roomLine})");
            ctx.Check(Patch_MouseoverReadout.LineFor(DepthRimFixture.Home, DepthRimFixture.Home.Center) == null, "no water line on the surface map");

            WeatherDef undersea = DefDatabase<WeatherDef>.GetNamed("DepthRim_Undersea");
            ctx.Check(map.Biome.baseWeatherCommonalities.All(w => w.weather == undersea), "the ocean biome only has the undersea weather");
            map.weatherManager.TransitionTo(undersea);
            yield return new WaitTicks(5);
            ctx.Check(map.weatherManager.curWeather == undersea, "the map's weather is undersea");
            ctx.Check(undersea.LabelCap.ToString() == "海底" || undersea.LabelCap.ToString().ToLowerInvariant() == "undersea", $"the weather reads {undersea.LabelCap}");
        }
    }
}
