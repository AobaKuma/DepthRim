using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse.AI;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>壓力適應與加壓艙（Hediff_PressureAdaptation、CompPressurizer、PressureChamberUtility）。</summary>
    public static class Tests_Pressure
    {
        private const int Day = PressureAdaptationMath.TicksPerDay;

        /// <summary>
        /// P5：在地表蓋一間 3×3 的加壓艙（耐壓甲板、水密隔牆、水密門、屋頂、加壓機）。
        /// 檢查：三個條件各自不符時不算加壓艙；加壓到 2 級後艙內是 2、艙外是 0；殖民者進艙後得到適應 hediff 並不適，
        /// 一天左右（依血液過濾能力）後適應值到 1、不適消失；出艙後沒有不適，適應值退回 0 後 hediff 移除；從海床上來的人直接出艙會嚴重不適，
        /// 在 2 級的艙裡則沒事；艙壁由牆體水壓系統判定（2 級時水密門撐不住、1 級時單層一般牆撐不住）；停機時艙壓不變、
        /// 一般的門會慢慢洩壓、破艙立刻失壓。時間用直接結算（Advance／UpdateChamber）推進，不真的等幾天。
        /// </summary>
        public static IEnumerable<Wait> P5_PressureAdaptation(TestContext ctx)
        {
            Map map = DepthRimFixture.Home;
            TerrainDef deck = DefDatabase<TerrainDef>.GetNamed("DepthRim_PressureDeck");
            ThingDef machineDef = DepthRimDefOf.DepthRim_Pressurizer;
            ThingDef hullWall = ThingDef.Named("DepthRim_PressureHullWall");
            ctx.Check(PressureEnvironment.AmbientOf(map) == 0f, "the surface map is at surface pressure");

            // 測試區：發射台東邊 6 格，5×5 的牆圈（內部 3×3）
            CellRect ring = new CellRect(DepthRimFixture.Pad.maxX + 6, DepthRimFixture.Pad.minZ, 5, 5);
            ctx.Require(ring.ExpandedBy(2).InBounds(map), "test area is inside the map");
            Dictionary<IntVec3, TerrainDef> terrainBefore = ring.ExpandedBy(1).Cells.ToDictionary(c => c, c => map.terrainGrid.TerrainAt(c));
            foreach (IntVec3 c in ring.ExpandedBy(2))
            {
                if (c.Roofed(map) && !c.Filled(map)) { map.roofGrid.SetRoof(c, null); }
            }
            foreach (IntVec3 c in ring.ExpandedBy(1))
            {
                DepthRimFixture.ClearCell(map, c, TerrainDefOf.Concrete);
            }
            CellRect inside = ring.ContractedBy(1);
            IntVec3 doorCell = new IntVec3(ring.CenterCell.x, 0, ring.minZ);
            IntVec3 outside = new IntVec3(ring.CenterCell.x, 0, ring.minZ - 1);

            ctx.Check(GenConstruct.CanPlaceBlueprintAt(deck, inside.CenterCell, Rot4.North, map).Accepted, "pressure deck can be laid on ordinary ground");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(machineDef, inside.CenterCell, Rot4.North, map).Accepted, "the pressuriser cannot stand on an ordinary floor");
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(DepthRimDefOf.DepthRim_WatertightDoor, doorCell, Rot4.North, map).Accepted, "a watertight door can be built on the surface");
            foreach (IntVec3 c in inside) { map.terrainGrid.SetTerrain(c, deck); }
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(machineDef, inside.CenterCell, Rot4.North, map).Accepted, "the pressuriser can stand on pressure deck");

            foreach (IntVec3 c in ring.EdgeCells)
            {
                if (c != doorCell) { DepthRimFixture.Spawn(hullWall, c, map); }
            }
            Thing door = DepthRimFixture.Spawn(DepthRimDefOf.DepthRim_WatertightDoor, doorCell, map);
            foreach (IntVec3 c in ring) { map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed); }
            IntVec3 machineCell = new IntVec3(inside.minX, 0, inside.maxZ);
            Thing machine = DepthRimFixture.Spawn(machineDef, machineCell, map);
            CompPressurizer comp = machine.TryGetComp<CompPressurizer>();
            CompPowerTrader power = machine.TryGetComp<CompPowerTrader>();
            ctx.Require(comp != null && power != null, "pressuriser spawned with its comps");
            yield return new WaitTicks(5);

            MapComponent_PressureChambers chambers = map.GetComponent<MapComponent_PressureChambers>();
            ctx.Require(chambers != null && chambers.Pressurizers.Contains(comp), "the pressuriser registered with the map");
            Room room = inside.CenterCell.GetRoom(map);
            ctx.Require(room != null && room.CellCount == 9, $"the chamber is one 3x3 room ({room?.CellCount})");

            void Run(int ticks)
            {
                power.PowerOn = true;
                comp.UpdateChamber(ticks);
            }

            // ── 加壓艙的三個條件 ──
            Run(250);
            ctx.Check(comp.Problem == null && comp.ChamberSealed, $"a sealed deck room with a watertight door is a pressure chamber ({comp.Problem})");

            IntVec3 bare = new IntVec3(inside.maxX, 0, inside.minZ);
            map.terrainGrid.SetTerrain(bare, TerrainDefOf.Concrete);
            Run(250);
            ctx.Check(comp.Problem == PressureChamberUtility.NeedsDeck, $"one cell of ordinary floor: needs deck ({comp.Problem})");
            map.terrainGrid.SetTerrain(bare, deck);

            map.roofGrid.SetRoof(inside.CenterCell, null);
            yield return new WaitTicks(3);
            Run(250);
            ctx.Check(comp.Problem == PressureChamberUtility.NotSealed, $"a hole in the roof: not sealed ({comp.Problem})");
            map.roofGrid.SetRoof(inside.CenterCell, RoofDefOf.RoofConstructed);

            door.Destroy(DestroyMode.Vanish);
            Thing plainDoor = DepthRimFixture.Spawn(ThingDefOf.Door, doorCell, map, ThingDefOf.Steel);
            yield return new WaitTicks(3);
            Run(250);
            ctx.Check(comp.Problem == PressureChamberUtility.NeedsWatertightDoors, $"an ordinary door: needs watertight doors ({comp.Problem})");
            plainDoor.Destroy(DestroyMode.Vanish);
            door = DepthRimFixture.Spawn(DepthRimDefOf.DepthRim_WatertightDoor, doorCell, map);
            yield return new WaitTicks(3);
            Run(250);
            ctx.Require(comp.Problem == null, $"chamber valid again ({comp.Problem})");
            ctx.Check(comp.CurrentLevel == 0f, "an idle chamber sits at the outside pressure");
            ctx.Check(comp.CompInspectStringExtra().Contains("0"), "inspect string shows the pressure");

            // ── 加壓 ──
            comp.TargetLevel = 2;
            Run(1250);
            ctx.Check(Mathf.Abs(comp.CurrentLevel - 0.5f) < 0.01f, $"half an hour raises the chamber half a level ({comp.CurrentLevel:F2})");
            Run(2500);
            Run(2500);
            ctx.Check(comp.CurrentLevel == 2f, $"the chamber reaches the target and stops ({comp.CurrentLevel:F2})");
            ctx.Check(PressureEnvironment.At(map, inside.CenterCell) == 2f, "pressure inside the chamber is 2");
            ctx.Check(PressureEnvironment.At(map, outside) == 0f, "pressure outside the chamber is still 0");
            comp.TargetLevel = 9;
            ctx.Check(comp.TargetLevel == PressureAdaptationMath.MaxLevel, "target level is clamped to the seabed level");
            comp.TargetLevel = 2;

            // ── 水密門：沒適應的人不會自己進艙 ──
            Building_WatertightDoor wdoor = (Building_WatertightDoor)door;
            wdoor.InvalidatePressureSides();
            ctx.Check(wdoor.PressureSides(out float sideLow, out float sideHigh) && sideLow == 0f && sideHigh == 2f, $"the door sees level 0 on one side and level 2 on the other ({sideLow}/{sideHigh})");
            ctx.Check(wdoor.GetInspectString().Contains("2.0"), "the door's inspect string shows the pressure difference");
            Pawn outsider = DepthRimFixture.SpawnColonist(map, outside);
            ctx.Check(wdoor.PawnCanOpen(outsider), "a drafted colonist may enter the chamber");
            outsider.drafter.Drafted = false;
            ctx.Check(!wdoor.PawnCanOpen(outsider), "an unadapted, undrafted colonist will not open the chamber door");
            ctx.Check(!map.reachability.CanReach(outsider.Position, inside.CenterCell, PathEndMode.OnCell, TraverseParms.For(outsider)), "so the chamber is unreachable for work and wandering");
            outsider.drafter.Drafted = true;
            MapComponent_WallPressure walls = map.GetComponent<MapComponent_WallPressure>();
            walls.UpdateNow();
            ctx.Check(walls.LastOverloadedCount == 1 && comp.OverloadedCount == 1, $"at level 2 the hull walls hold but the watertight door does not ({walls.LastOverloadedCount} overloaded)");

            // ── 殖民者在艙裡適應 ──
            Pawn pawn = DepthRimFixture.SpawnColonist(map, inside.CenterCell);
            ctx.Check(PressureAdaptationUtility.Get(pawn) == null, "a surface colonist starts with no adaptation hediff");
            Run(250);
            Hediff_PressureAdaptation hediff = PressureAdaptationUtility.Get(pawn);
            ctx.Require(hediff != null, "standing in a pressurised chamber adds the adaptation hediff");
            float filtration = hediff.BloodFiltration;
            float days = PressureAdaptationMath.DaysPerLevel(filtration);
            ctx.Note($"colonist blood filtration {filtration:P0}: {days:F2} days per level");
            ctx.Check(days >= 0.5f && days <= 2f, "days per level within 0.5-2");
            ctx.Check(hediff.Adapted == 0f && hediff.Ambient == 2f, $"player pawn starts unadapted (adapted {hediff.Adapted}, surroundings {hediff.Ambient})");
            ctx.Check(Mathf.Abs(hediff.Severity - 1f) < 0.01f && hediff.CurStageIndex == 2, $"two levels off: pressure sickness (severity {hediff.Severity:F2}, stage {hediff.CurStageIndex})");
            float consciousnessSick = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness);
            ctx.Check(hediff.LabelInBrackets.Contains("0.0"), $"label shows the adapted level ({hediff.LabelInBrackets})");
            ctx.Check(!hediff.TipStringExtra.NullOrEmpty(), "tooltip explains the rate");

            int half = Mathf.RoundToInt(days * Day * 0.5f);
            hediff.Advance(half);
            ctx.Check(Mathf.Abs(hediff.Adapted - 0.5f) < 0.01f, $"half the time: adapted 0.5 ({hediff.Adapted:F2})");
            ctx.Check(hediff.CurStageIndex == 2 || hediff.CurStageIndex == 1, "discomfort eases as the body catches up");
            hediff.Advance(half + 250);
            ctx.Check(hediff.Adapted >= 1f && hediff.Mismatch == 0f, $"after {days:F1} days the mismatch is gone (adapted {hediff.Adapted:F2})");
            ctx.Check(hediff.CurStageIndex == 0, "no discomfort stage once within one level");
            ctx.Check(pawn.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness) > consciousnessSick, "consciousness recovers");
            ctx.Check(pawn.health.hediffSet.hediffs.Contains(hediff), "the hediff stays to remember the adapted level");

            // ── 出艙：差 1 級以內沒事，慢慢退回 0 後移除 ──
            // ── 水密門：適應中的人留在艙裡，適應到差 1 級以內才出得去 ──
            pawn.drafter.Drafted = false;
            hediff.SetState(2f, 2f);
            wdoor.InvalidatePressureSides();
            ctx.Check(!wdoor.PawnCanOpen(pawn), "someone adapted to level 2 will not walk out to the surface on their own");
            hediff.SetState(1.5f, 2f);
            ctx.Check(!wdoor.PawnCanOpen(pawn), "still held at adapted 1.5");
            hediff.SetState(1f, 2f);
            ctx.Check(wdoor.PawnCanOpen(pawn), "adapted down to 1: the door lets them out");
            hediff.SetState(0f, 2f);
            ctx.Check(wdoor.PawnCanOpen(pawn), "someone suffering inside is always allowed out to where they are fine");
            pawn.drafter.Drafted = true;

            // ── 開門洩壓 ──
            float beforeOpen = comp.CurrentLevel;
            wdoor.StartManualOpenBy(pawn);
            ctx.Check(Mathf.Abs(beforeOpen - comp.CurrentLevel - PressureAdaptationMath.DoorOpeningLeak) < 0.02f, $"opening the door lets a little pressure out ({beforeOpen:F2} -> {comp.CurrentLevel:F2})");
            power.PowerOn = false;
            comp.UpdateChamber(250);
            ctx.Check(comp.CurrentLevel < beforeOpen - PressureAdaptationMath.DoorOpeningLeak - 0.1f, $"while the door stands open the chamber keeps losing pressure ({comp.CurrentLevel:F2})");
            yield return new WaitUntil(() => !wdoor.Open, 600);
            ctx.Require(!wdoor.Open, "the door closed again");
            Run(2500);
            Run(2500);
            ctx.Require(comp.CurrentLevel == 2f, $"repressurised after the door closed ({comp.CurrentLevel:F2})");

            // 剛好適應到 1 級再出艙（上面多結算了 250 ticks，適應值略高於 1）
            hediff.SetState(1f, 2f);
            pawn.Position = outside;
            hediff.Advance(250);
            ctx.Check(hediff.Ambient == 0f && hediff.Mismatch == 0f, $"stepping out one level down causes no discomfort (adapted {hediff.Adapted:F3}, surroundings {hediff.Ambient}, mismatch {hediff.Mismatch:F3})");
            hediff.Advance(Mathf.RoundToInt(days * Day * 1.2f));
            ctx.Check(hediff.Adapted == 0f && hediff.ShouldRemove, "back at surface pressure the hediff is ready to go");
            yield return new WaitTicks(300);
            ctx.Check(PressureAdaptationUtility.Get(pawn) == null, "the game removes the hediff once fully re-adapted");

            // ── 從海床上來的人 ──
            Hediff_PressureAdaptation deep = PressureAdaptationUtility.Ensure(pawn, 0f);
            deep.SetState(3f, 0f);
            ctx.Check(Mathf.Abs(deep.Severity - 2f) < 0.01f && deep.CurStageIndex == 3, $"seabed crew straight onto the surface: severe sickness (severity {deep.Severity:F2})");
            pawn.Position = inside.CenterCell;
            deep.Advance(250);
            ctx.Check(deep.Ambient == 2f && deep.Mismatch == 0f && deep.CurStageIndex == 0, "inside the level-2 chamber the same person is fine");
            ctx.Check(!deep.ShouldRemove, "an adapted body keeps the hediff");

            // ── 其他陣營視為本來就適應 ──
            Pawn visitor = null;
            Faction other = Find.FactionManager.AllFactions.First(f => !f.IsPlayer && f.def.humanlikeFaction && !f.Hidden);
            visitor = PawnGenerator.GeneratePawn(new PawnGenerationRequest(other.RandomPawnKind(), other, forceGenerateNewPawn: true));
            GenSpawn.Spawn(visitor, inside.CenterCell, map, WipeMode.Vanish);
            Hediff_PressureAdaptation native = PressureAdaptationUtility.Ensure(visitor, 2f);
            ctx.Check(native != null && native.Adapted == 2f && native.Mismatch == 0f, "a non-player pawn starts adapted to where it is");
            visitor.Destroy(DestroyMode.Vanish);

            // ── 警示 ──
            Alert_PressureSickness alert = new Alert_PressureSickness();
            ctx.Check(!alert.SickColonists.Contains(pawn), "no alert for someone inside the chamber that suits them");
            deep.SetState(3f, 0f);
            ctx.Check(alert.SickColonists.Contains(pawn) && alert.GetReport().active, "alert lists a colonist with pressure sickness");
            ctx.Check(alert.GetExplanation().ToString().Contains(pawn.LabelShortCap), "alert explanation names them");
            deep.Advance(250);
            ctx.Check(!alert.SickColonists.Contains(pawn), "alert clears once the surroundings suit them again");

            // ── 艙壁耐壓：和外海同一套（牆體水壓系統）──
            void Walls(int n) { for (int k = 0; k < n; k++) { walls.UpdateNow(); } }
            int doorHp = door.HitPoints;
            Walls(4);
            ctx.Check(door.HitPoints < doorHp, $"at level 2 the watertight door is slowly crushed ({doorHp} -> {door.HitPoints})");
            ctx.Check(comp.CompInspectStringExtra().Contains("1"), "the pressuriser reports the failing door");
            door.HitPoints = door.MaxHitPoints;
            comp.TargetLevel = 1;
            Run(2500);
            ctx.Require(Mathf.Abs(comp.CurrentLevel - 1f) < 0.01f, $"down to level 1 ({comp.CurrentLevel:F2})");
            Walls(1);
            ctx.Check(walls.LastOverloadedCount == 0, $"at level 1 the watertight door holds ({walls.LastOverloadedCount})");
            IntVec3 weakCell = new IntVec3(ring.minX, 0, ring.CenterCell.z);
            weakCell.GetEdifice(map).Destroy(DestroyMode.Vanish);
            Thing plainWall = DepthRimFixture.Spawn(ThingDefOf.Wall, weakCell, map, ThingDefOf.Steel);
            yield return new WaitTicks(3);
            Run(250);
            ctx.Require(comp.Problem == null, $"still a chamber with one ordinary wall ({comp.Problem})");
            int hp0 = plainWall.HitPoints;
            Walls(4);
            ctx.Check(walls.LastOverloadedCount == 1 && plainWall.HitPoints < hp0, $"a single ordinary wall cannot hold level 1 against the surface ({walls.LastOverloadedCount}, {hp0} -> {plainWall.HitPoints})");
            if (!plainWall.Destroyed) { plainWall.Destroy(DestroyMode.Vanish); }
            DepthRimFixture.Spawn(hullWall, weakCell, map);
            comp.TargetLevel = 2;
            yield return new WaitTicks(3);
            Run(2500);
            Run(2500);
            ctx.Require(comp.Problem == null && comp.CurrentLevel == 2f, $"chamber restored with a hull wall ({comp.Problem}, {comp.CurrentLevel:F2})");

            // ── 停機：密閉的艙室記住原有壓力 ──
            power.PowerOn = false;
            comp.UpdateChamber(2500 * 4);
            ctx.Check(comp.CurrentLevel == 2f, $"switched off, a sealed chamber keeps its pressure ({comp.CurrentLevel:F2})");
            ctx.Check(PressureEnvironment.At(map, inside.CenterCell) == 2f, "and still counts as level 2");

            // ── 撐不住壓力的房間：慢慢洩壓 ──
            door.Destroy(DestroyMode.Vanish);
            plainDoor = DepthRimFixture.Spawn(ThingDefOf.Door, doorCell, map, ThingDefOf.Steel);
            yield return new WaitTicks(3);
            power.PowerOn = false;
            comp.UpdateChamber(2500);
            ctx.Check(Mathf.Abs(comp.CurrentLevel - (2f - PressureAdaptationMath.UnheldLeakPerHour)) < 0.02f, $"with an ordinary door the room leaks {PressureAdaptationMath.UnheldLeakPerHour} levels an hour ({comp.CurrentLevel:F2})");
            plainDoor.Destroy(DestroyMode.Vanish);
            door = DepthRimFixture.Spawn(DepthRimDefOf.DepthRim_WatertightDoor, doorCell, map);
            yield return new WaitTicks(3);

            // ── 牆破掉：立刻失壓 ──
            Run(2500);
            Run(2500);
            ctx.Check(comp.CurrentLevel == 2f, "repressurised");
            map.roofGrid.SetRoof(inside.CenterCell, null);
            yield return new WaitTicks(3);
            Run(250);
            ctx.Check(comp.CurrentLevel == 0f && PressureEnvironment.At(map, inside.CenterCell) == 0f && !chambers.AnyPressure, "a breach drops the chamber to the outside pressure at once");

            // 還原
            // 先拆掉站在甲板上的東西，再把地形換回去
            if (!machine.Destroyed) { machine.Destroy(DestroyMode.Vanish); }
            if (!door.Destroyed) { door.Destroy(DestroyMode.Vanish); }
            foreach (IntVec3 c in ring) { map.roofGrid.SetRoof(c, null); }
            foreach (KeyValuePair<IntVec3, TerrainDef> kv in terrainBefore) { map.terrainGrid.SetTerrain(kv.Key, kv.Value); }
        }

        /// <summary>
        /// P6：水下地圖上的艙壓。冰洋測試地圖上蓋一間一般鋼牆的密閉艙室並抽乾，暫時換成深海。
        /// 檢查：乾艙維持常壓（不是地圖層級）、外海是 2 級；把艙壓設成 2 級後和外海同壓、牆不受力；換到海床後艙壓不變（記住原有壓力）、
        /// 牆又開始超載；地板不是耐壓甲板時慢慢洩壓、鋪上後保持；潛艇搬動時的艙壓複製（Harmony 掛在 DMSE 的 PlaceOnShip）。
        /// </summary>
        public static IEnumerable<Wait> P6_CompartmentPressure(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_WallPressure walls = map.GetComponent<MapComponent_WallPressure>();
            MapComponent_PressureChambers chambers = map.GetComponent<MapComponent_PressureChambers>();
            Room room = Tests_Flooding.BuildCompartment(ctx, out CellRect inner, out List<Thing> wallThings);
            foreach (Wait w in Tests_Flooding.DrainCompartment(ctx, inner)) { yield return w; }
            room = inner.CenterCell.GetRoom(map);
            ctx.Require(room != null && Tests_Flooding.Level(inner.CenterCell) <= 0.01f, "a dry sealed compartment");
            IntVec3 sea = DepthRimFixture.OpenWaterCell();
            Dictionary<IntVec3, TerrainDef> floor = inner.Cells.ToDictionary(c => c, c => map.terrainGrid.TerrainAt(c));

            PlanetTile tile = map.Tile;
            BiomeDef original = tile.Tile.PrimaryBiome;
            tile.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("DepthRim_SeabedPlain");
            try
            {
                ctx.Require(DepthRimFixture.Flooding.DepthTier == DepthTier.Deep, "the test map is now deep");
                ctx.Check(PressureEnvironment.At(map, sea) == 2f, $"open water in the deep is level 2 ({PressureEnvironment.At(map, sea)})");
                ctx.Check(PressureEnvironment.At(map, inner.CenterCell) == 0f, "a dry compartment keeps surface pressure, whatever the depth");
                walls.MarkDirty();
                walls.UpdateNow();
                int overloadedDry = walls.LastOverloadedCount;
                ctx.Check(overloadedDry > 0, $"so its plain walls carry the full sea pressure ({overloadedDry} overloaded)");

                // 艙壓 2 級 = 深海的水壓：牆不受力
                chambers.SetRoomAir(room, 2f);
                ctx.Check(PressureEnvironment.At(map, inner.CenterCell) == 2f, "a compartment set to level 2 is level 2");
                walls.UpdateNow();
                ctx.Check(walls.LastOverloadedCount == 0, $"at sea pressure the walls take no load ({walls.LastOverloadedCount})");

                // 地板不是耐壓甲板：撐不住，慢慢洩壓
                chambers.UpdateNow(2500);
                ctx.Check(Mathf.Abs(chambers.AirOf(room) - (2f - PressureAdaptationMath.UnheldLeakPerHour)) < 0.02f, $"on a seabed floor the compartment leaks ({chambers.AirOf(room):F2})");
                TerrainDef deck = DefDatabase<TerrainDef>.GetNamed("DepthRim_PressureDeck");
                foreach (IntVec3 c in inner) { map.terrainGrid.SetTerrain(c, deck); }
                chambers.SetRoomAir(room, 2f);
                chambers.UpdateNow(2500 * 4);
                ctx.Check(chambers.AirOf(room) == 2f, $"on pressure deck it holds ({chambers.AirOf(room):F2})");

                // 換到海床：艙壓不變，牆又超載
                tile.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("DepthRim_AbyssalTrench");
                ctx.Check(DepthRimFixture.Flooding.DepthTier == DepthTier.Seabed, $"the test map is now the seabed ({DepthRimFixture.Flooding.DepthTier})");
                chambers.UpdateNow(250);
                ctx.Check(chambers.AirOf(room) == 2f && PressureEnvironment.At(map, inner.CenterCell) == 2f, "going deeper, the compartment keeps its own pressure");
                ctx.Check(PressureEnvironment.At(map, sea) == 3f, "while the sea is level 3");
                walls.UpdateNow();
                ctx.Check(walls.LastOverloadedCount > 0, $"so the walls carry the difference again ({walls.LastOverloadedCount})");

                // 殖民者在這間艙裡開始適應
                Pawn pawn = DepthRimFixture.SpawnColonist(map, inner.CenterCell);
                chambers.UpdateNow(250);
                Hediff_PressureAdaptation hediff = PressureAdaptationUtility.Get(pawn);
                ctx.Check(hediff != null && hediff.Ambient == 2f, $"a colonist in the level-2 compartment starts adapting to 2 ({hediff?.Ambient})");

                // 潛艇搬動：掛在 DMSE 的 HullPlaced 事件（HullMoveHooks）；搬過去的格子帶著艙壓、原地歸零
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(HullMoveHooks).TypeHandle);
                System.Delegate hullPlaced = (System.Delegate)HarmonyLib.AccessTools.Field(typeof(DMSE.ShipSeparationUtility), "HullPlaced").GetValue(null);
                ctx.Check(hullPlaced != null && hullPlaced.GetInvocationList().Any(d => d.Method.DeclaringType == typeof(HullMoveHooks)), "the air pressure carry hook is on DMSE's HullPlaced event");
                MapComponent_PressureChambers home = DepthRimFixture.Home.GetComponent<MapComponent_PressureChambers>();
                IntVec3 offset = DepthRimFixture.Pad.CenterCell - inner.CenterCell;
                home.CopyFrom(chambers, new[] { inner.CenterCell }, offset);
                ctx.Check(Mathf.Abs(home.AirAt(DepthRimFixture.Pad.CenterCell) - 2f) < 0.02f, "a carried cell arrives with its pressure");
                ctx.Check(chambers.AirAt(inner.CenterCell) == 0f, "and leaves none behind");
                home.UpdateNow(250);
                ctx.Check(home.AirAt(DepthRimFixture.Pad.CenterCell) == 0f, "outdoors on the surface it drops to 0 at the next update");
            }
            finally
            {
                tile.Tile.PrimaryBiome = original;
                chambers.SetRoomAir(inner.CenterCell.GetRoom(map), 0f);
                foreach (KeyValuePair<IntVec3, TerrainDef> kv in floor) { map.terrainGrid.SetTerrain(kv.Key, kv.Value); }
            }
            chambers.UpdateNow(250);
            ctx.Check(!chambers.AnyPressure, "no pressure left on the test map");
            yield return new WaitTicks(5);
        }
    }
}
