using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>艙室水位（設計稿 §2.2、MapComponent_Flooding）。每個測試在冰洋地圖上自己圍一間艙室。</summary>
    public static class Tests_Flooding
    {
        private const int Update = MapComponent_Flooding.UpdateInterval;

        private static WaitTicks Updates(int n) => new WaitTicks(Update * n + 5);

        /// <summary>圍出一間 5×5 的密閉艙室（7×7 牆圈）；回傳內部格子之一與牆。</summary>
        private static Room BuildCompartment(TestContext ctx, out CellRect inner, out List<Thing> walls)
        {
            CellRect ring = DepthRimFixture.ClearOceanArea(7);
            walls = DepthRimFixture.BuildWallRing(ring);
            inner = ring.ContractedBy(1);
            Room room = inner.CenterCell.GetRoom(DepthRimFixture.Ocean);
            ctx.Require(room != null, "compartment room exists");
            ctx.Require(room.CellCount == inner.Area, $"compartment has {inner.Area} cells ({room.CellCount})");
            ctx.Require(!DepthRimFixture.Flooding.IsOpenWater(room), "compartment is sealed (roofed, not touching the edge)");
            return room;
        }

        private static float Level(IntVec3 c) => DepthRimFixture.Flooding.LevelAt(c);

        /// <summary>放一台通電的排水泵（下方一格放電線讓它接上電網）。</summary>
        private static CompFloodPump SpawnPump(IntVec3 cell, IntVec3 conduitCell)
        {
            DepthRimFixture.Spawn(ThingDefOf.PowerConduit, conduitCell, DepthRimFixture.Ocean);
            Thing pump = DepthRimFixture.Spawn(DepthRimFixture.BilgePump, cell, DepthRimFixture.Ocean);
            return pump.TryGetComp<CompFloodPump>();
        }

        private static IEnumerable<Wait> DrainCompartment(TestContext ctx, CellRect inner)
        {
            CompFloodPump pump = SpawnPump(inner.CenterCell, inner.CenterCell + IntVec3.North);
            WaitUntil powered = new WaitUntil(() => pump.Working, 1200);
            yield return powered;
            ctx.Require(!powered.TimedOut, "pump powered");
            WaitUntil dry = new WaitUntil(() => Level(inner.CenterCell) <= 0.01f, 6000);
            yield return dry;
            ctx.Require(!dry.TimedOut, $"compartment drained (level {Level(inner.CenterCell):P0})");
            pump.parent.TryGetComp<CompFlickable>().DoFlick();
            yield return Updates(1);
            ctx.Require(!pump.Working, "pump switched off");
        }

        public static IEnumerable<Wait> F1_OpenWater(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            int roofed = map.AllCells.Count(c => c.Roofed(map));
            int thick = map.AllCells.Count(c => c.GetRoof(map)?.isThickRoof ?? false);
            var rooms = map.regionGrid.AllRooms.Where(r => !r.IsDoorway).ToList();
            ctx.Note($"cells {map.cellIndices.NumGridCells}, roofed {roofed} (thick {thick}), rooms {rooms.Count}, edge rooms {rooms.Count(r => r.TouchesMapEdge)}, rooms with open roof {rooms.Count(r => r.OpenRoofCount > 0)}");
            IntVec3 cell = DepthRimFixture.OpenWaterCell();
            ctx.Require(cell.IsValid, "found an open-water cell");
            yield return Updates(2);
            ctx.Check(Level(cell) >= 0.999f, $"open water level 100% ({Level(cell):P1})");
            Room room = cell.GetRoom(DepthRimFixture.Ocean);
            ctx.Check(DepthRimFixture.Flooding.LevelOf(room) == 1f, "LevelOf(open room) is 1");
            ctx.Check(cell.WaterLevelAt(DepthRimFixture.Home) == 0f, "surface map has no water");
        }

        public static IEnumerable<Wait> F2_SealAndDrain(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            Room room = BuildCompartment(ctx, out CellRect inner, out _);
            yield return Updates(1);
            ctx.Check(Level(inner.CenterCell) >= 0.99f, $"new compartment inherits the flooded cells ({Level(inner.CenterCell):P0})");

            CompFloodPump pump = SpawnPump(inner.CenterCell, inner.CenterCell + IntVec3.North);
            WaitUntil powered = new WaitUntil(() => pump.Working, 1200);
            yield return powered;
            ctx.Require(!powered.TimedOut, "pump powered");
            ctx.Check(pump.DrainedRoom == inner.CenterCell.GetRoom(DepthRimFixture.Ocean), "pump drains its own compartment");

            float before = Level(inner.CenterCell);
            yield return Updates(2);
            float after = Level(inner.CenterCell);
            ctx.Check(after < before, $"level drops while pumping ({before:P0} -> {after:P0})");
            int start = Find.TickManager.TicksGame;
            WaitUntil dry = new WaitUntil(() => Level(inner.CenterCell) <= 0.01f, 6000);
            yield return dry;
            ctx.Check(!dry.TimedOut, $"compartment drained ({Level(inner.CenterCell):P0})");
            ctx.Note($"drain time after the first two updates: {Find.TickManager.TicksGame - start} ticks for {inner.Area} cells");
            ctx.Check(inner.Cells.All(c => Level(c) == Level(inner.CenterCell)), "every cell of the compartment has the same level");
            ctx.Check(pump.CompInspectStringExtra() != null, "pump shows the compartment level");
            Current.Game.CurrentMap = DepthRimFixture.Ocean;
            CameraJumper.TryJump(inner.CenterCell, DepthRimFixture.Ocean);
            yield return new WaitFrames(10);
            ctx.Screenshot("drained_compartment");
        }

        public static IEnumerable<Wait> F3_DamagedWallLeaks(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            BuildCompartment(ctx, out CellRect inner, out List<Thing> walls);
            foreach (Wait w in DrainCompartment(ctx, inner)) { yield return w; }
            float dry = Level(inner.CenterCell);

            // 外牆中段（不是角落）打到剩 10%
            Thing wall = walls.First(w => w.Position.z == inner.minZ - 1 && w.Position.x == inner.CenterCell.x);
            wall.HitPoints = Math.Max(1, wall.MaxHitPoints / 10);
            yield return Updates(3);
            float leaked = Level(inner.CenterCell);
            ctx.Check(leaked > dry, $"damaged wall leaks ({dry:P0} -> {leaked:P0})");
            // 等待 755 ticks 可能跨過 3 或 4 次更新，上限以 4 次計
            float expected = FloodingMath.LeakVolume(wall.HitPoints, wall.MaxHitPoints, DepthTier.Shallow, 1f, 0f, Update / 60f) * 4f / inner.Area;
            ctx.Check(leaked <= expected * 1.2f + 0.01f, $"leak is no faster than the formula allows ({leaked:P1} vs {expected:P1})");

            wall.HitPoints = wall.MaxHitPoints;
            yield return Updates(1);
            float repaired = Level(inner.CenterCell);
            yield return Updates(2);
            ctx.Check(Level(inner.CenterCell) <= repaired + 0.005f, "a repaired wall stops leaking");
        }

        public static IEnumerable<Wait> F4_OpenDoor(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            BuildCompartment(ctx, out CellRect inner, out List<Thing> walls);
            foreach (Wait w in DrainCompartment(ctx, inner)) { yield return w; }

            // 把外牆中段換成門；門外清成開放水域通道直到原本的大洞穴
            Thing wall = walls.First(w => w.Position.z == inner.minZ - 1 && w.Position.x == inner.CenterCell.x);
            IntVec3 doorCell = wall.Position;
            wall.Destroy(DestroyMode.Vanish);
            Building_Door door = (Building_Door)DepthRimFixture.Spawn(ThingDefOf.Door, doorCell, DepthRimFixture.Ocean, ThingDefOf.Steel);
            yield return Updates(2);
            ctx.Check(Level(inner.CenterCell) <= 0.02f, $"closed door keeps the compartment dry ({Level(inner.CenterCell):P0})");

            // 門外是清出來的區域（仍有岩頂、原本就淹水），確認另一側有水
            Map map = DepthRimFixture.Ocean;
            IntVec3 outside = doorCell + IntVec3.South;
            Room outsideRoom = outside.GetRoom(map);
            float outsideLevel = DepthRimFixture.Flooding.LevelOf(outsideRoom);
            ctx.Require(outsideRoom != null && outsideLevel > 0.5f, $"water on the other side of the door ({outsideLevel:P0})");

            AccessField(door, "openInt", true);
            AccessField(door, "holdOpenInt", true);
            ctx.Require(door.Open, "door held open");
            yield return Updates(3);
            ctx.Check(Level(inner.CenterCell) > 0.3f, $"open door floods the compartment ({Level(inner.CenterCell):P0})");
            AccessField(door, "holdOpenInt", false);
        }

        public static IEnumerable<Wait> F5_Breach(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            BuildCompartment(ctx, out CellRect inner, out List<Thing> walls);
            foreach (Wait w in DrainCompartment(ctx, inner)) { yield return w; }
            Map map = DepthRimFixture.Ocean;

            // 打穿外牆並把外側的屋頂拿掉，艙室直接連到開放水域
            Thing wall = walls.First(w => w.Position.z == inner.minZ - 1 && w.Position.x == inner.CenterCell.x);
            IntVec3 breach = wall.Position;
            wall.Destroy(DestroyMode.KillFinalize);
            map.roofGrid.SetRoof(breach, null);
            yield return Updates(1);
            Room merged = inner.CenterCell.GetRoom(map);
            ctx.Check(DepthRimFixture.Flooding.IsOpenWater(merged), "breached compartment is connected to the open sea");
            // 使用者要求：打通後要「進水」而不是一瞬間灌滿（淺海約 20 秒從乾到滿）
            float first = Level(inner.CenterCell);
            ctx.Check(first > 0f && first < 0.999f, $"the sea pours in gradually, not instantly ({first:P0} after one update)");
            ctx.Check(!DepthRimFixture.Flooding.IsOpenAt(inner.CenterCell), "a cell that is still filling is not open water yet (drawn by the compartment layer)");
            ctx.Check(DepthRimFixture.Flooding.InflowCells.Count > 0, "the breach front is an inflow point (spray and flood sound)");
            WaitUntil full = new WaitUntil(() => Level(inner.CenterCell) >= 0.995f, MapComponent_Flooding.UpdateInterval * 12);
            yield return full;
            ctx.Check(!full.TimedOut, $"the breached compartment fills completely ({Level(inner.CenterCell):P0})");
            yield return Updates(1);
            ctx.Check(DepthRimFixture.Flooding.IsOpenAt(inner.CenterCell), "once full it becomes open water");
        }

        /// <summary>只存讀 MapComponent_Flooding 的資料，不重新載入整個遊戲。</summary>
        public static IEnumerable<Wait> S1_ScribeRoundTrip(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            BuildCompartment(ctx, out CellRect inner, out _);
            foreach (Wait w in DrainCompartment(ctx, inner)) { yield return w; }
            MapComponent_Flooding live = DepthRimFixture.Flooding;
            IntVec3 open = DepthRimFixture.OpenWaterCell();

            string path = Path.Combine(Path.GetTempPath(), "depthrim-flooding-" + Guid.NewGuid() + ".xml");
            try
            {
                Scribe.saver.InitSaving(path, "flooding");
                live.ExposeData();
                Scribe.saver.FinalizeSaving();

                MapComponent_Flooding loaded = new MapComponent_Flooding(DepthRimFixture.Ocean);
                Scribe.loader.InitLoading(path);
                loaded.ExposeData();
                Scribe.loader.FinalizeLoading();
                loaded.FinalizeInit();

                ctx.Check(loaded.Active, "loaded component is active");
                ctx.Check(Math.Abs(loaded.LevelAt(inner.CenterCell) - live.LevelAt(inner.CenterCell)) < 0.005f, $"drained compartment level restored ({loaded.LevelAt(inner.CenterCell):P0})");
                ctx.Check(Math.Abs(loaded.LevelAt(open) - live.LevelAt(open)) < 0.005f, "open water level restored");
                int diff = DepthRimFixture.Ocean.AllCells.Count(c => Math.Abs(loaded.LevelAt(c) - live.LevelAt(c)) > 0.005f);
                ctx.Check(diff == 0, $"every cell restored ({diff} differ)");
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        private static void AccessField(object obj, string name, object value)
        {
            HarmonyLib.AccessTools.Field(obj.GetType(), name).SetValue(obj, value);
        }
    }
}
