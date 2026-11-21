using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>水面航行的離港與進場（2026-11-21）：出航時在原本的地圖上播離港演出；抵達有地圖的海面地點時先選停靠位置、再播進場演出。</summary>
    public static class Tests_VoyageShows
    {
        private static bool ShowOpen() => HarborCutscene.Last != null && Find.WindowStack.IsOpen(HarborCutscene.Last);

        private static IEnumerable<Wait> FramesUntil(System.Func<bool> condition, int maxFrames)
        {
            for (int i = 0; i < maxFrames && !condition(); i++)
            {
                yield return new WaitFrames(1);
            }
        }

        /// <summary>
        /// C7：兩格相鄰的海（都是開放海面）。起點的海面地點上除了船還有一根電線柱（所以是共用的地圖），終點的海面地點也有一根。
        /// 從起點出航 → 起點地圖上播離港演出（真的船體已經在船地圖上），演出結束鏡頭跟到船上；
        /// 抵達終點 → 先出現選停靠位置，選之前不播、不搬 → 選好播進場演出，演出結束船體停在選的位置。演出要遊戲視窗在前景才會跑。
        /// </summary>
        public static IEnumerable<Wait> C7_VoyageShows(TestContext ctx)
        {
            PlanetLayer surface = Find.WorldGrid.FirstLayerOfDef(PlanetLayerDefOf.Surface);
            PlanetTile t0 = PlanetTile.Invalid, n1 = PlanetTile.Invalid;
            List<PlanetTile> nb = new List<PlanetTile>();
            for (int id = surface.TilesCount / 2; id < surface.TilesCount && !n1.Valid; id += 11)
            {
                PlanetTile c = new PlanetTile(id, surface);
                if (Find.WorldObjects.AnyWorldObjectAt(c)) { continue; }
                nb.Clear();
                Find.WorldGrid.GetTileNeighbors(c, nb);
                PlanetTile free = nb.FirstOrDefault(n => !Find.WorldObjects.AnyWorldObjectAt(n));
                if (free.Valid)
                {
                    t0 = c;
                    n1 = free;
                }
            }
            ctx.Require(n1.Valid, "found two free neighbouring tiles");
            Dictionary<PlanetTile, (BiomeDef biome, float elevation, float temperature)> before = new[] { t0, n1 }
                .ToDictionary(t => t, t => (t.Tile.PrimaryBiome, t.Tile.elevation, t.Tile.temperature));
            foreach (PlanetTile t in new[] { t0, n1 })
            {
                t.Tile.PrimaryBiome = BiomeDefOf.Ocean;
                t.Tile.elevation = -500f;
                t.Tile.temperature = 15f;
            }
            Find.World.tileTemperatures.ClearCaches();
            SurfaceRouting.ClearCache();

            Map viewing = Find.CurrentMap;
            bool skipBefore = SubmarineSurfacing.skipCutscenes;
            bool prefBefore = Prefs.GravshipCutscenes;
            System.Type keepBefore = AcceptanceDriver.KeepWindowOpen;
            SubmarineSurfacing.skipCutscenes = false;
            Prefs.GravshipCutscenes = true;
            OverboardHazard.debugSeaFactor = 1f;
            List<MapParent> sites = new List<MapParent>();
            try
            {
                Map sea = DebugActions_SeaSurface.CreateAt(t0);
                ctx.Require(sea != null, "a sea-surface site at the start");
                sites.Add(sea.Parent);
                Map dock = DebugActions_SeaSurface.CreateAt(n1);
                ctx.Require(dock != null, "a sea-surface site at the destination");
                sites.Add(dock.Parent);
                Thing Build(ThingDef def, IntVec3 at, Map map)
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                    t.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(t, at, map, Rot4.North);
                    return t;
                }
                // 兩張地圖上各一根不屬於船的電線柱，地圖才是「共用的」、船走了也留著
                Build(ThingDefOf.PowerConduit, new IntVec3(8, 0, 8), sea);
                Build(ThingDefOf.PowerConduit, new IntVec3(8, 0, 8), dock);
                Current.Game.CurrentMap = sea;
                TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
                CellRect deck = CellRect.CenteredOn(sea.Center, 4);
                // 船艏朝北（推進器朝北推）、尖的：甲板北邊再接三列，一列比一列窄
                List<IntVec3> hullCells = deck.Cells.ToList();
                for (int row = 1; row <= 3; row++)
                {
                    for (int dx = -(3 - row); dx <= 3 - row; dx++)
                    {
                        hullCells.Add(new IntVec3(deck.CenterCell.x + dx, 0, deck.maxZ + row));
                    }
                }
                foreach (IntVec3 c in hullCells) { sea.terrainGrid.SetTerrain(c, platform); }
                foreach (IntVec3 c in deck) { Build(ThingDefOf.PowerConduit, c, sea); }
                IntVec3 center = deck.CenterCell;
                Build(ThingDef.Named("DepthRim_Keel"), center, sea);
                Thing helm = Build(ThingDef.Named("DepthRim_Helm"), center + new IntVec3(-2, 0, 3), sea);
                Thing thruster = Build(DepthRimDefOf.DepthRim_SurfaceThruster, center + new IntVec3(3, 0, -2), sea);
                Thing battery = Build(ThingDef.Named("Battery"), center + new IntVec3(-3, 0, -3), sea);
                battery.TryGetComp<CompPowerBattery>().AddEnergy(5000f);
                MapComponent_PileNetwork.For(sea).MarkDirty();
                yield return new WaitUntil(() => helm.TryGetComp<CompPowerTrader>().PowerOn && thruster.TryGetComp<CompPowerTrader>().PowerOn, 1200);
                string block = SurfaceVoyage.BlockReason(helm, out _);
                ctx.Require(block == null, $"the ship can sail ({block})");

                // ── 出航：離港演出 ──
                AcceptanceDriver.KeepWindowOpen = typeof(HarborCutscene);
                HarborCutscene.Last = null;
                ctx.Require(SurfaceVoyage.Depart(helm, n1), "departure started");
                foreach (Wait w in FramesUntil(() => ShowOpen() && HarborCutscene.Last.Progress > 0.05f, 900)) { yield return w; }
                HarborCutscene leaving = HarborCutscene.Last;
                ctx.Check(leaving != null && leaving.Departing, "departing plays the leaving show");
                ctx.Check(helm.Map?.Parent is WorldObject_SurfaceShip, "the real hull is already on the ship's map during the show");
                ctx.Check(Find.CurrentMap == sea, "the show plays on the map the ship is leaving");
                foreach (Wait w in FramesUntil(() => !ShowOpen(), 2400)) { yield return w; }
                ctx.Check(!ShowOpen(), "the leaving show finishes");
                WorldObject_SurfaceShip ship = helm.Map?.Parent as WorldObject_SurfaceShip;
                ctx.Require(ship != null && ship.underWay, "the ship is under way");
                ctx.Check(Find.CurrentMap == ship.Map, "after the show the view follows the ship");

                // 航行中的航跡：船艏浪貼著尖船艏（截圖看）
                MapComponent_SeaSurface seaComp = ship.Map.GetComponent<MapComponent_SeaSurface>();
                yield return new WaitUntil(() => seaComp.WakeSpeed > 0.05f, 600);
                seaComp.SnapWake();
                ctx.Check(seaComp.WakeHull.valid && seaComp.WakeHull.bowProfile != null && seaComp.WakeHull.bowProfile[0] < seaComp.WakeHull.halfWidth - 1f,
                    $"the wake knows the bow is pointed (tip half-width {seaComp.WakeHull.bowProfile?[0]:0.0} vs {seaComp.WakeHull.halfWidth:0.0})");
                UnityEngine.Vector4 bowA = seaComp.Material.GetVector("_BowA");
                ctx.Check(UnityEngine.Mathf.Abs(bowA.x - seaComp.WakeHull.bowProfile[0]) < 1e-3f, "and the shader gets the bow profile");
                IntVec3 shipCenter = CellRect.FromCellList(ship.Map.AllCells.Where(c => MapComponent_SeabedMemory.IsPlatform(ship.Map.terrainGrid.TerrainAt(c)))).CenterCell;
                foreach (Wait w in ctx.ScreenshotOn(ship.Map, shipCenter + new IntVec3(0, 0, 6), 14f, "wake_pointed_bow", 30)) { yield return w; }

                // ── 抵達：先選位，再播進場演出 ──
                // 船上一個殖民者拿著 10 鋼、站在一堆 20 鋼上（2026-11-21 log：靠岸旋轉時搬運中的堆被併掉又重生）
                Map shipMap = ship.Map;
                IntVec3 deckCell = CellRect.FromCellList(shipMap.AllCells.Where(c => MapComponent_SeabedMemory.IsPlatform(shipMap.terrainGrid.TerrainAt(c)))).CenterCell + new IntVec3(1, 0, -3);
                Thing pile = ThingMaker.MakeThing(ThingDefOf.Steel);
                pile.stackCount = 20;
                GenSpawn.Spawn(pile, deckCell, shipMap);
                Pawn hauler = DepthRimFixture.SpawnColonist(shipMap, deckCell);
                Thing carried = ThingMaker.MakeThing(ThingDefOf.Steel);
                carried.stackCount = 10;
                ctx.Check(hauler.carryTracker.TryStartCarry(carried), "the colonist carries 10 steel");
                int SteelOn(Map m) => m.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned).Sum(t => t.stackCount)
                    + m.mapPawns.AllPawnsSpawned.Sum(p => p.carryTracker?.CarriedThing?.def == ThingDefOf.Steel ? p.carryTracker.CarriedThing.stackCount : 0);
                int steelBefore = SteelOn(shipMap);
                ship.AdvanceTicks(ship.ticksPerTile);
                yield return new WaitRealtime(() => Find.DesignatorManager.SelectedDesignator is Designator_DockShip, 15f);
                Designator_DockShip berth = Find.DesignatorManager.SelectedDesignator as Designator_DockShip;
                ctx.Require(berth != null, "arriving asks where to moor");
                ctx.Check(!ShowOpen() && helm.Map != dock, "nothing plays or moves before the choice");
                yield return new WaitRealtime(() => false, 0.5f);
                IntVec3 spot = GenRadial.RadialCellsAround(dock.Center + new IntVec3(-20, 0, 15), 20f, true).FirstOrDefault(c => c.InBounds(dock) && berth.CanDesignateCell(c).Accepted);
                ctx.Require(spot.IsValid, "found a free berth");
                HarborCutscene.Last = null;
                // 轉一次（旋轉時 Pawn 與東西要一起轉）
                HarmonyLib.Traverse.Create(berth).Field("turns").SetValue(1);
                spot = GenRadial.RadialCellsAround(dock.Center + new IntVec3(-20, 0, 15), 20f, true).FirstOrDefault(c => c.InBounds(dock) && berth.CanDesignateCell(c).Accepted);
                ctx.Require(spot.IsValid, "found a free berth for the turned ship");
                int dockSteelBefore = SteelOn(dock);
                berth.DesignateSingleCell(spot);
                foreach (Wait w in FramesUntil(() => ShowOpen() && HarborCutscene.Last.Progress > 0.05f, 900)) { yield return w; }
                HarborCutscene arriving = HarborCutscene.Last;
                ctx.Check(arriving != null && !arriving.Departing, "the choice plays the arriving show");
                ctx.Check(helm.Map != dock, "the real hull waits until the show's last frame");
                foreach (Wait w in FramesUntil(() => !ShowOpen(), 2400)) { yield return w; }
                yield return new WaitUntil(() => helm.Map == dock, 600);
                ctx.Check(helm.Map == dock, "after the show the ship is moored in the site's map");
                IntVec3 deckAt = CellRect.FromCellList(dock.AllCells.Where(c => MapComponent_SeabedMemory.IsPlatform(dock.terrainGrid.TerrainAt(c)))).CenterCell;
                ctx.Check(deckAt.DistanceTo(spot) <= 1.5f, $"at the chosen berth ({deckAt} vs {spot})");
                int steelAfter = SteelOn(dock) - dockSteelBefore;
                ctx.Check(steelAfter == steelBefore, $"turning and mooring keeps all the steel ({steelAfter} of {steelBefore})");
                ctx.Check(hauler.Spawned && hauler.Map == dock, "the colonist came along");
                ctx.Check(!dock.listerThings.ThingsOfDef(ThingDefOf.Steel).Any(t => t.stackCount <= 0 || t.Destroyed), "no empty or destroyed steel stacks on the map");
                foreach (Wait w in ctx.ScreenshotOn(dock, deckAt, 20f, "moored_after_show")) { yield return w; }
            }
            finally
            {
                AcceptanceDriver.KeepWindowOpen = keepBefore;
                SubmarineSurfacing.skipCutscenes = skipBefore;
                Prefs.GravshipCutscenes = prefBefore;
                OverboardHazard.debugSeaFactor = null;
                Find.DesignatorManager.Deselect();
                Current.Game.CurrentMap = viewing;
                foreach (MapParent p in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_SeaSurfaceSite>().Where(s => s.Tile == t0 || s.Tile == n1).Cast<MapParent>().Concat(sites).Distinct().ToList())
                {
                    if (p.HasMap) { Current.Game.DeinitAndRemoveMap(p.Map, false); }
                    if (!p.Destroyed) { p.Destroy(); }
                }
                foreach (KeyValuePair<PlanetTile, (BiomeDef biome, float elevation, float temperature)> kv in before)
                {
                    kv.Key.Tile.PrimaryBiome = kv.Value.biome;
                    kv.Key.Tile.elevation = kv.Value.elevation;
                    kv.Key.Tile.temperature = kv.Value.temperature;
                }
                Find.World.tileTemperatures.ClearCaches();
                SurfaceRouting.ClearCache();
            }
            yield return new WaitFrames(2);
        }

        /// <summary>
        /// C8（2026-11-21）：雙體船的航跡。兩個 15×5 的船身（上面那個是尖船艏）、中間 5×5 的橋接，往東開。
        /// 船艏浪與船尾翻攪水照實際的船體格畫：兩個船身各自有、中間的空檔沒有（截圖看）。
        /// </summary>
        public static IEnumerable<Wait> C8_CatamaranWake(TestContext ctx)
        {
            PlanetLayer surface = Find.WorldGrid.FirstLayerOfDef(PlanetLayerDefOf.Surface);
            PlanetTile t0 = PlanetTile.Invalid, n1 = PlanetTile.Invalid;
            List<PlanetTile> nb = new List<PlanetTile>();
            for (int id = surface.TilesCount / 3; id < surface.TilesCount && !n1.Valid; id += 13)
            {
                PlanetTile c = new PlanetTile(id, surface);
                if (Find.WorldObjects.AnyWorldObjectAt(c)) { continue; }
                nb.Clear();
                Find.WorldGrid.GetTileNeighbors(c, nb);
                PlanetTile free = nb.FirstOrDefault(n => !Find.WorldObjects.AnyWorldObjectAt(n));
                if (free.Valid)
                {
                    t0 = c;
                    n1 = free;
                }
            }
            ctx.Require(n1.Valid, "found two free neighbouring tiles");
            Dictionary<PlanetTile, (BiomeDef biome, float elevation, float temperature)> before = new[] { t0, n1 }
                .ToDictionary(t => t, t => (t.Tile.PrimaryBiome, t.Tile.elevation, t.Tile.temperature));
            foreach (PlanetTile t in new[] { t0, n1 })
            {
                t.Tile.PrimaryBiome = BiomeDefOf.Ocean;
                t.Tile.elevation = -500f;
                t.Tile.temperature = 15f;
            }
            Find.World.tileTemperatures.ClearCaches();
            SurfaceRouting.ClearCache();
            Map viewing = Find.CurrentMap;
            bool skipBefore = SubmarineSurfacing.skipCutscenes;
            SubmarineSurfacing.skipCutscenes = true;
            OverboardHazard.debugSeaFactor = 1f;
            try
            {
                Map sea = DebugActions_SeaSurface.CreateAt(t0);
                ctx.Require(sea != null, "a sea-surface site");
                Current.Game.CurrentMap = sea;
                Thing Build(ThingDef def, IntVec3 at, Rot4 rot)
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                    t.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(t, at, sea, rot);
                    return t;
                }
                IntVec3 c0 = sea.Center;
                TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
                List<IntVec3> hull = new List<IntVec3>();
                for (int x = -7; x <= 7; x++)
                {
                    for (int z = 3; z <= 7; z++) { hull.Add(c0 + new IntVec3(x, 0, z)); hull.Add(c0 + new IntVec3(x, 0, -z)); }
                }
                for (int x = -2; x <= 2; x++)
                {
                    for (int z = -2; z <= 2; z++) { hull.Add(c0 + new IntVec3(x, 0, z)); }
                }
                // 上面那個船身的尖船艏（往東）
                for (int z = 4; z <= 6; z++) { hull.Add(c0 + new IntVec3(8, 0, z)); }
                hull.Add(c0 + new IntVec3(9, 0, 5));
                foreach (IntVec3 c in hull) { sea.terrainGrid.SetTerrain(c, platform); }
                foreach (IntVec3 c in hull) { Build(ThingDefOf.PowerConduit, c, Rot4.North); }
                Build(ThingDef.Named("DepthRim_Keel"), c0, Rot4.North);
                Thing helm = Build(ThingDef.Named("DepthRim_Helm"), c0 + new IntVec3(-3, 0, 5), Rot4.North);
                Thing thruster = Build(DepthRimDefOf.DepthRim_SurfaceThruster, c0 + new IntVec3(-5, 0, -5), Rot4.East);
                Thing battery = Build(ThingDef.Named("Battery"), c0 + new IntVec3(3, 0, -6), Rot4.North);
                battery.TryGetComp<CompPowerBattery>().AddEnergy(5000f);
                MapComponent_PileNetwork.For(sea).MarkDirty();
                yield return new WaitUntil(() => helm.TryGetComp<CompPowerTrader>().PowerOn && thruster.TryGetComp<CompPowerTrader>().PowerOn, 1200);
                string block = SurfaceVoyage.BlockReason(helm, out SurfaceVoyage.Plan plan);
                ctx.Require(block == null, $"the catamaran can sail ({block})");
                ctx.Check(plan.footprint.Count == hull.Count, $"both hulls and the bridge are one ship ({plan.footprint.Count}/{hull.Count})");
                ctx.Require(SurfaceVoyage.Depart(helm, n1), "departure started");
                yield return new WaitUntil(() => helm.Map?.Parent is WorldObject_SurfaceShip s && s.underWay, 600);
                WorldObject_SurfaceShip ship = helm.Map?.Parent as WorldObject_SurfaceShip;
                ctx.Require(ship != null, "under way");
                MapComponent_SeaSurface seaComp = ship.Map.GetComponent<MapComponent_SeaSurface>();
                yield return new WaitUntil(() => seaComp.WakeSpeed > 0.05f, 600);
                seaComp.SnapWake();
                ctx.Check(ship.MapHeading.HasValue && ship.MapHeading.Value.x > 0.99f, $"the east-facing thruster drives it east ({ship.MapHeading})");
                IntVec3 mid = CellRect.FromCellList(ship.Map.AllCells.Where(c => MapComponent_SeabedMemory.IsPlatform(ship.Map.terrainGrid.TerrainAt(c)))).CenterCell;
                Current.Game.CurrentMap = ship.Map;
                yield return new WaitFrames(10);
                ctx.Note($"current map for the screenshot: {(Find.CurrentMap == ship.Map ? "the ship" : Find.CurrentMap?.ToString())}");
                foreach (Wait w in ctx.ScreenshotOn(ship.Map, mid + new IntVec3(-6, 0, 0), 16f, "catamaran_wake", 30)) { yield return w; }
                // 截圖晚一幀才寫檔：在切回原本的地圖之前多等幾幀
                yield return new WaitFrames(30);
            }
            finally
            {
                SubmarineSurfacing.skipCutscenes = skipBefore;
                OverboardHazard.debugSeaFactor = null;
                Current.Game.CurrentMap = viewing;
                foreach (MapParent p in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_SeaSurfaceSite>().Where(x => x.Tile == t0 || x.Tile == n1).Cast<MapParent>().ToList())
                {
                    if (p.HasMap) { Current.Game.DeinitAndRemoveMap(p.Map, false); }
                    if (!p.Destroyed) { p.Destroy(); }
                }
                foreach (KeyValuePair<PlanetTile, (BiomeDef biome, float elevation, float temperature)> kv in before)
                {
                    kv.Key.Tile.PrimaryBiome = kv.Value.biome;
                    kv.Key.Tile.elevation = kv.Value.elevation;
                    kv.Key.Tile.temperature = kv.Value.temperature;
                }
                Find.World.tileTemperatures.ClearCaches();
                SurfaceRouting.ClearCache();
            }
            yield return new WaitFrames(2);
        }
    }
}
