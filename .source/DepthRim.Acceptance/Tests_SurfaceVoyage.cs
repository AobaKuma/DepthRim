using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>水面航行 M2（Docs/海岸船舶與潛水鐘_機制設計.md §4）。</summary>
    public static class Tests_SurfaceVoyage
    {
        /// <summary>
        /// C2：把地表上三格（T0–N1–N2，N2 不和 T0 相鄰）設成開放海面，T0 的另一個鄰格設成海冰；在 T0 建海面地點、鋪 9×9 船體、
        /// 蓋龍骨、艦橋、水面推進器、電池，放一個殖民者。檢查：
        /// 出航前檢查（沒電、兩座艦橋連在一起）、目的地檢查（原地、海冰）、出航後船體連人搬進航行中的船（原地點被移除）、
        /// 航行中只畫艙內、水面推進器全速耗電、不能下潛；逐格前進、推進器關掉就漂著不動；到達空海域停下來；
        /// 再開到另一個海面地點時停靠進那張地圖、船物件移除。最後還原世界格與地圖。
        /// </summary>
        public static IEnumerable<Wait> C2_SurfaceVoyage(TestContext ctx)
        {
            PlanetLayer surface = Find.WorldGrid.FirstLayerOfDef(PlanetLayerDefOf.Surface);
            ctx.Require(surface != null, "the surface layer exists");
            PlanetTile t0 = PlanetTile.Invalid, n1 = PlanetTile.Invalid, n2 = PlanetTile.Invalid, ice = PlanetTile.Invalid;
            List<PlanetTile> nb = new List<PlanetTile>();
            List<PlanetTile> nb2 = new List<PlanetTile>();
            for (int id = surface.TilesCount / 3; id < surface.TilesCount && !n2.Valid; id += 7)
            {
                PlanetTile c = new PlanetTile(id, surface);
                if (Find.WorldObjects.AnyWorldObjectAt(c)) { continue; }
                nb.Clear();
                Find.WorldGrid.GetTileNeighbors(c, nb);
                List<PlanetTile> free = nb.Where(n => !Find.WorldObjects.AnyWorldObjectAt(n)).ToList();
                if (free.Count < 2) { continue; }
                foreach (PlanetTile a in free)
                {
                    nb2.Clear();
                    Find.WorldGrid.GetTileNeighbors(a, nb2);
                    PlanetTile b = nb2.FirstOrDefault(x => x != c && !nb.Contains(x) && !Find.WorldObjects.AnyWorldObjectAt(x));
                    if (b.Valid)
                    {
                        t0 = c; n1 = a; n2 = b; ice = free.First(x => x != a);
                        break;
                    }
                }
            }
            ctx.Require(n2.Valid, "found three free tiles in a row");
            ctx.Note($"route tiles {t0} -> {n1} -> {n2}, ice {ice}");
            Dictionary<PlanetTile, (BiomeDef biome, float elevation, float temperature)> before = new[] { t0, n1, n2, ice }
                .ToDictionary(t => t, t => (t.Tile.PrimaryBiome, t.Tile.elevation, t.Tile.temperature));
            foreach (PlanetTile t in new[] { t0, n1, n2 })
            {
                t.Tile.PrimaryBiome = BiomeDefOf.Ocean;
                t.Tile.elevation = -500f;
                t.Tile.temperature = 15f;
            }
            ice.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("SeaIce");
            ice.Tile.elevation = -500f;
            ice.Tile.temperature = -25f;
            Find.World.tileTemperatures.ClearCaches();
            SurfaceRouting.ClearCache();

            Map viewing = Find.CurrentMap;
            // 海面天氣是隨機的：大浪會把沒扣繩的人捲下甲板（A2），這裡只測航行，固定成平靜的海
            OverboardHazard.debugSeaFactor = 1f;
            List<MapParent> sites = new List<MapParent>();
            WorldObject_SurfaceShip ship = null;
            Pawn sailor = null;
            try
            {
                ctx.Check(SurfaceRouting.Navigable(t0) && SurfaceRouting.Navigable(n1) && SurfaceRouting.Navigable(n2), "the three tiles are open sea");
                ctx.Check(!SurfaceRouting.Navigable(ice), "sea ice is not navigable");
                ctx.Check(SurfaceRouting.Distance(t0, n2) == 2, $"the route is two tiles ({SurfaceRouting.Distance(t0, n2)})");
                List<PlanetTile> path = SurfaceRouting.Path(t0, n2);
                ctx.Check(path != null && path.Count == 3 && path[0] == t0 && path[2] == n2 && path.All(SurfaceRouting.Navigable), "the path stays on open sea");

                // ── 海面地點與船 ──
                Map sea = DebugActions_SeaSurface.CreateAt(t0);
                ctx.Require(sea != null && SeaLevels.IsSeaLevel(sea), "a sea-surface site at the start tile");
                sites.Add(sea.Parent);
                Current.Game.CurrentMap = sea;
                TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
                CellRect deck = CellRect.CenteredOn(sea.Center, 4);
                foreach (IntVec3 c in deck) { sea.terrainGrid.SetTerrain(c, platform); }
                foreach (IntVec3 c in deck)
                {
                    Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                    conduit.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(conduit, c, sea);
                }
                Thing Build(ThingDef def, IntVec3 at)
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                    t.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(t, at, sea, Rot4.North);
                    return t;
                }
                IntVec3 center = deck.CenterCell;
                Thing keel = Build(ThingDef.Named("DepthRim_Keel"), center);
                Thing helm = Build(ThingDef.Named("DepthRim_Helm"), center + new IntVec3(-2, 0, 3));
                Thing thruster = Build(DepthRimDefOf.DepthRim_SurfaceThruster, center + new IntVec3(3, 0, -2));
                Thing battery = Build(ThingDef.Named("Battery"), center + new IntVec3(-3, 0, -3));
                sailor = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(sailor, center + new IntVec3(0, 0, -2), sea);
                if (sailor.drafter != null) { sailor.drafter.Drafted = true; }
                MapComponent_PileNetwork.For(sea).MarkDirty();
                yield return new WaitTicks(5);

                string noPower = SurfaceVoyage.BlockReason(helm, out _);
                ctx.Check(noPower != null, $"with flat batteries the ship cannot sail ({noPower})");
                battery.TryGetComp<CompPowerBattery>().AddEnergy(5000f);
                CompPowerTrader helmPower = helm.TryGetComp<CompPowerTrader>();
                CompPowerTrader thrusterPower = thruster.TryGetComp<CompPowerTrader>();
                yield return new WaitUntil(() => helmPower.PowerOn && thrusterPower.PowerOn, 1200);
                ctx.Check(helmPower.PowerOn && thrusterPower.PowerOn, "helm and thruster powered by the battery");

                Thing helm2 = Build(ThingDef.Named("DepthRim_Helm"), center + new IntVec3(2, 0, 3));
                MapComponent_PileNetwork.For(sea).MarkDirty();
                ctx.Check(SurfaceVoyage.BlockReason(helm, out _) == "DepthRim.Sail.ShipsLinked".Translate().ToString(), "two helms on one keel network: two ships joined, cannot sail");
                helm2.Destroy();
                MapComponent_PileNetwork.For(sea).MarkDirty();

                string block = SurfaceVoyage.BlockReason(helm, out SurfaceVoyage.Plan plan);
                ctx.Require(block == null, $"the ship can sail ({block})");
                ctx.Check(plan.footprint.Count == deck.Area && plan.thrust > 0f && plan.range >= 2, $"plan: {plan.footprint.Count} cells, thrust {plan.thrust}, range {plan.range}");
                ctx.Check(SurfaceVoyage.DestinationBlockReason(t0, t0, plan.range) == "DepthRim.Sail.Here".Translate().ToString(), "the start tile is not a destination");
                ctx.Check(SurfaceVoyage.DestinationBlockReason(t0, ice, plan.range) == "DepthRim.Sail.NotOpenSea".Translate().ToString(), "sea ice is not a destination");
                ctx.Check(SurfaceVoyage.DestinationBlockReason(t0, n2, plan.range) == null, "the tile two away is a valid destination");
                ctx.Check(SurfaceVoyage.DestinationBlockReason(t0, n2, 1) != null, "out of range is refused");
                ctx.Check(SubmarineNavigation.SurfacedOptions(helm).Any(o => o.Label == "DepthRim.Sail.Option".Translate().ToString() && !o.Disabled), "the helm's navigate menu offers Sail to");
                ctx.Check(SubmarineNavigation.NavigateBlockReason(helm) == null, "and the navigate command is enabled");

                // ── 出航 ──
                ctx.Require(SurfaceVoyage.Depart(helm, n2), "departure started");
                yield return new WaitUntil(() => helm.Map?.Parent is WorldObject_SurfaceShip, 600);
                ship = helm.Map?.Parent as WorldObject_SurfaceShip;
                ctx.Require(ship != null, "the hull moved onto a ship under way");
                Map shipMap = ship.Map;
                ctx.Note($"after departure: current map is {(Find.CurrentMap == shipMap ? "the ship" : Find.CurrentMap == sea ? "the start site" : Find.CurrentMap?.ToString())}");
                ctx.Check(Find.CurrentMap == shipMap, "the view follows the ship");
                string named = helm.TryGetComp<CompTransferConsole>()?.ShipName;
                ctx.Check(!named.NullOrEmpty() && ship.Label == named && named.StartsWith("DepthRim.Sail.DefaultName".Translate("").ToString().Trim()),
                    $"an unnamed ship gets a ship's default name, kept on the helm ({ship.Label})");
                ctx.Check(ship.underWay && ship.Tile == t0 && ship.Destination == n2 && ship.route.Count == 3, $"under way from the start toward {n2} ({ship.route.Count} route tiles)");
                ctx.Check(new[] { keel, thruster, battery }.All(t => t.Spawned && t.Map == shipMap), "keel, thruster and battery came aboard");
                ctx.Check(sailor.Spawned && sailor.Map == shipMap, "the colonist on deck came along");
                ctx.Check(shipMap.AllCells.Count(c => MapComponent_SeabedMemory.IsPlatform(shipMap.terrainGrid.TerrainAt(c))) == deck.Area, "the whole deck is on the ship map");
                ctx.Check(!VoyageView.IsVoyageMap(shipMap), "under way the sea stays visible (no submarine-style fade)");
                ctx.Check(SubmarineSurfacing.DiveBlockReason(helm, out _, out _) == "DepthRim.Sail.StopFirst".Translate().ToString(), "cannot dive while under way");
                ctx.Check(SubmarineNavigation.SurfacedOptions(helm).Any(o => o.Label == "DepthRim.Sail.Stop".Translate().ToString()), "the navigate menu offers Stop here");
                ctx.Check(thruster.TryGetComp<CompThrusterDraw>().UnderWay, "the surface thruster runs at full draw under way");
                ctx.Check(ship.ExpandingIconRotation != 0f || ship.NextTile.Valid, "the world icon follows the heading");
                MapParent oldSite = sites[0];
                yield return new WaitUntil(() => oldSite.Destroyed, 1500);
                if (!oldSite.Destroyed && oldSite.HasMap)
                {
                    Map old = oldSite.Map;
                    ctx.Note($"start site kept: buildings [{string.Join(", ", old.listerBuildings.allBuildingsColonist.Select(b => b.def.defName + "@" + b.Position))}], "
                        + $"blocking pawns {old.mapPawns.AnyPawnBlockingMapRemoval} [{string.Join(", ", old.mapPawns.AllPawnsSpawned.Select(p => p.LabelShort + "@" + p.Position))}], current {Find.CurrentMap == old}");
                }
                ctx.Check(oldSite.Destroyed, "the emptied start site is removed");
                // ── 航跡 ──
                MapComponent_SeaSurface seaComp = shipMap.GetComponent<MapComponent_SeaSurface>();
                ctx.Require(seaComp != null && seaComp.Material != null, "the ship map has a sea-surface material");
                // 航跡強度以真實時間平滑（約 2 秒），測試以高倍速跑：先確認有在變強，再直接設到目標值
                yield return new WaitUntil(() => seaComp.WakeSpeed > 0.1f, 1200);
                ctx.Check(seaComp.WakeSpeed > 0.1f, $"the wake starts building up under way ({seaComp.WakeSpeed:0.00})");
                seaComp.SnapWake();
                ctx.Check(seaComp.WakeSpeed >= 0.99f, $"at full thrust the wake's target is full strength ({seaComp.WakeSpeed:0.00})");
                UnityEngine.Vector2 heading = ship.MapHeading ?? UnityEngine.Vector2.zero;
                ctx.Check((seaComp.WakeHeading - heading).sqrMagnitude < 1e-4f, $"the wake follows the world heading ({seaComp.WakeHeading} vs {heading})");
                SeaWake.Hull hullBox = seaComp.WakeHull;
                ctx.Check(hullBox.valid && hullBox.length >= deck.Width - 0.01f && hullBox.length <= deck.Width * 1.45f && hullBox.halfWidth >= deck.Width * 0.5f - 0.01f,
                    $"the wake's hull outline matches the deck (length {hullBox.length:0.0}, half width {hullBox.halfWidth:0.0})");
                UnityEngine.Vector4 voyage = seaComp.Material.GetVector("_Voyage");
                ctx.Check(UnityEngine.Mathf.Abs(voyage.z - seaComp.WakeSpeed) < 0.01f && seaComp.Material.GetVector("_Wake").z > 0f, "the wake parameters reach the sea shader");
                UnityEngine.Vector2 flow1 = seaComp.WakeFlow;
                yield return new WaitTicks(120);
                UnityEngine.Vector2 moved = seaComp.WakeFlow - flow1;
                ctx.Check(UnityEngine.Vector2.Dot(moved, heading) > 1f, $"the water flows past the ship toward the stern ({moved})");
                foreach (Wait w in ctx.ScreenshotOn(shipMap, center - new IntVec3(UnityEngine.Mathf.RoundToInt(heading.x * 8f), 0, UnityEngine.Mathf.RoundToInt(heading.y * 8f)), 22f, "surface_voyage_wake")) { yield return w; }
                yield return new WaitFrames(30);

                // ── 逐格前進、沒推力就漂 ──
                CompFlickable flick = thruster.TryGetComp<CompFlickable>();
                flick.SwitchIsOn = false;
                yield return new WaitTicks(5);
                ship.AdvanceTicks(ship.ticksPerTile);
                ctx.Check(ship.stalled && ship.Tile == t0, "with the thruster off the ship drifts in place");
                flick.SwitchIsOn = true;
                yield return new WaitUntil(() => thrusterPower.PowerOn, 600);
                ship.AdvanceTicks(ship.ticksPerTile);
                ctx.Check(!ship.stalled && ship.Tile == n1 && ship.underWay, $"one tile's worth of sailing reaches the next tile ({ship.Tile})");
                ship.AdvanceTicks(ship.ticksPerTile);
                ctx.Check(!ship.underWay && ship.Tile == n2 && !ship.Destroyed, $"the ship stops at the empty destination ({ship.Tile})");
                yield return new WaitFrames(5);
                float before0 = seaComp.WakeSpeed;
                yield return new WaitFrames(10);
                ctx.Check(seaComp.WakeSpeed < before0 || before0 <= 0f, $"stopped: the wake starts dying down ({before0:0.00} -> {seaComp.WakeSpeed:0.00})");
                seaComp.SnapWake();
                ctx.Check(seaComp.WakeSpeed <= 0f, $"stopped: the wake's target is zero ({seaComp.WakeSpeed:0.00})");
                UnityEngine.Vector2 flowStopped = seaComp.WakeFlow;
                yield return new WaitTicks(60);
                ctx.Check((seaComp.WakeFlow - flowStopped).sqrMagnitude < 1e-6f, "stopped: the water no longer flows past");
                ctx.Check(!thruster.TryGetComp<CompThrusterDraw>().UnderWay, "stopped: the thruster idles at standby");

                // ── 開到另一個海面地點：停靠進去 ──
                Map dock = DebugActions_SeaSurface.CreateAt(n1);
                sites.Add(dock.Parent);
                Thing post = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                post.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(post, new IntVec3(5, 0, 5), dock);
                Current.Game.CurrentMap = shipMap;
                ctx.Require(SurfaceVoyage.Depart(helm, n1), "departure toward the other site");
                yield return new WaitUntil(() => helm.Map?.Parent is WorldObject_SurfaceShip s && s.underWay, 600);
                WorldObject_SurfaceShip ship2 = helm.Map?.Parent as WorldObject_SurfaceShip;
                ctx.Require(ship2 != null, "under way again");
                ship2.AdvanceTicks(ship2.ticksPerTile);
                yield return new WaitUntil(() => helm.Map == dock, 600);
                ctx.Check(helm.Map == dock && keel.Map == dock && sailor.Map == dock, "arriving at a site moors the ship into its map, with the crew");
                ctx.Check(ship2.Destroyed, "the ship object is gone once moored");
                ctx.Check(dock.AllCells.Count(c => MapComponent_SeabedMemory.IsPlatform(dock.terrainGrid.TerrainAt(c))) == deck.Area, "the whole deck is at the site");
                ship = null;
            }
            finally
            {
                OverboardHazard.debugSeaFactor = null;
                Current.Game.CurrentMap = viewing;
                if (sailor != null && !sailor.Destroyed) { if (sailor.Spawned) { sailor.DeSpawn(); } sailor.Destroy(); }
                foreach (MapParent p in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_SeaSurfaceSite>().Where(s => new[] { t0, n1, n2 }.Contains(s.Tile)).Cast<MapParent>().Concat(sites).Distinct().ToList())
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
                OceanRouting.ClearCache();
            }
            yield return new WaitFrames(2);
        }
    }
}
