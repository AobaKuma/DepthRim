using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>靠岸 M3（Docs/海岸船舶與潛水鐘_機制設計.md §5）。</summary>
    public static class Tests_Docking
    {
        private static bool NoBlockers(Map map, IntVec3 c) => c.GetThingList(map).All(t => t is Plant || t.def.category == ThingCategory.Filth);

        /// <summary>
        /// C3：在殖民地地圖邊上挖一個 25×30 的灣（原版深海水，接到地圖邊）、把殖民地旁邊一格設成開放海面；在那一格的海面地點造一艘 9×7 的船
        /// （龍骨、艦橋、水面推進器、電池、殖民者、貨物、甲板上的儲存區）。檢查：
        /// 艦橋選單有靠岸；泊位檢查（陸地不行、2×1 不能轉的排水泵擋住 90° 但不擋 180°、灣裡的泊位有迴轉空間與從地圖邊進來的路線）；
        /// 靠岸轉 90°：整具船體連人帶貨、儲存區照轉好的位置搬進灣裡，艦橋朝向跟著轉，龍骨網路完整，泊位記下來；
        /// 再下水：從記下的那一格海出去，灣恢復成水。最後還原。
        /// </summary>
        public static IEnumerable<Wait> C3_Docking(TestContext ctx) => Scenario(ctx, show: false);

        /// <summary>
        /// C4（M4 演出）：同 C3 的場景，但播演出：靠岸演出中真的船體還沒進殖民地、影像從進場口開進泊位（擷取進場與側推兩張截圖）、
        /// 最後一幀才搬進來；下水播離港演出（倒著播）；同一個泊位再靠一次只播短版。
        /// </summary>
        public static IEnumerable<Wait> C4_DockingShow(TestContext ctx) => Scenario(ctx, show: true);

        /// <summary>演出期間遊戲暫停（tick 不走），所以用畫面數等。</summary>
        private static IEnumerable<Wait> FramesUntil(System.Func<bool> condition, int maxFrames)
        {
            for (int i = 0; i < maxFrames && !condition(); i++)
            {
                yield return new WaitFrames(1);
            }
        }

        /// <summary>演出影像的位置離泊位中心多遠（格）。</summary>
        private static void Vector3Ish(HarborShowMath.Pose pose, List<IntVec3> berthCells, out float distance)
        {
            UnityEngine.Vector3 c = CellRect.FromCellList(berthCells).CenterVector3;
            distance = (pose.pos - new UnityEngine.Vector2(c.x, c.z)).magnitude;
        }

        private static bool ShowOpen() => HarborCutscene.Last != null && Find.WindowStack.IsOpen(HarborCutscene.Last);

        private static IEnumerable<Wait> Scenario(TestContext ctx, bool show)
        {
            bool skipBefore = SubmarineSurfacing.skipCutscenes;
            System.Type keepBefore = AcceptanceDriver.KeepWindowOpen;
            SubmarineSurfacing.skipCutscenes = !show;
            if (show)
            {
                AcceptanceDriver.KeepWindowOpen = typeof(HarborCutscene);
            }
            Map home = DepthRimFixture.Home;
            yield return new WaitFrames(2);
            TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            TerrainDef deep = TerrainDefOf.WaterOceanDeep;

            // ── 灣 ──
            const int BayW = 25, BayD = 30;
            // 測試地圖的邊上常有岩壁、礦塊：挑一段沒有人、沒有玩家建築、障礙最少的邊，整段清掉（岩壁、屋頂、雜物）
            bool Forbidden(IntVec3 c) => c.GetThingList(home).Any(t => t is Pawn || t.Faction == Faction.OfPlayer) || home.terrainGrid.UnderTerrainAt(c) != null
                || MapComponent_SeabedMemory.IsPlatform(home.terrainGrid.TerrainAt(c));
            int Obstacles(CellRect r) => r.Cells.Count(c => !NoBlockers(home, c) || home.roofGrid.Roofed(c));
            CellRect bay = CellRect.Empty;
            IntVec3 inward = IntVec3.Zero;
            int fewest = int.MaxValue;
            foreach (Rot4 edge in new[] { Rot4.South, Rot4.North, Rot4.West, Rot4.East })
            {
                int span = edge == Rot4.South || edge == Rot4.North ? home.Size.x : home.Size.z;
                for (int along = 10; along + BayW < span - 10; along += 5)
                {
                    CellRect r = edge == Rot4.South ? new CellRect(along, 0, BayW, BayD)
                        : edge == Rot4.North ? new CellRect(along, home.Size.z - BayD, BayW, BayD)
                        : edge == Rot4.West ? new CellRect(0, along, BayD, BayW)
                        : new CellRect(home.Size.x - BayD, along, BayD, BayW);
                    if (!r.InBounds(home) || r.ExpandedBy(3).ClipInsideMap(home).Cells.Any(Forbidden))
                    {
                        continue;
                    }
                    int n = Obstacles(r);
                    if (n < fewest)
                    {
                        fewest = n;
                        bay = r;
                        inward = edge == Rot4.South ? IntVec3.North : edge == Rot4.North ? IntVec3.South : edge == Rot4.West ? IntVec3.East : IntVec3.West;
                    }
                }
            }
            ctx.Require(bay.Area > 0, "found a strip at a map edge for a bay (no colonists or player buildings)");
            ctx.Note($"bay strip had {fewest} obstacle cells (cleared)");
            Dictionary<IntVec3, TerrainDef> original = bay.Cells.ToDictionary(c => c, c => home.terrainGrid.TerrainAt(c));
            foreach (IntVec3 c in bay)
            {
                foreach (Thing t in c.GetThingList(home).ToList()) { if (!t.Destroyed) { t.Destroy(); } }
                home.roofGrid.SetRoof(c, null);
                home.terrainGrid.SetTerrain(c, deep);
            }
            foreach (IntVec3 c in bay) { home.fogGrid.Unfog(c); }
            // 泊位中心：灣裡面、離地圖邊 22 格
            IntVec3 edgeMid = bay.CenterCell - inward * (BayD / 2);
            IntVec3 berthCenter = edgeMid + inward * 22;
            ctx.Note($"bay {bay}, berth centre {berthCenter}, inward {inward}");

            // ── 旁邊一格海 ──
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(home.Tile, neighbors);
            Dictionary<PlanetTile, (BiomeDef biome, float elevation, float temperature)> tileBefore = neighbors.ToDictionary(
                n => n, n => (n.Tile.PrimaryBiome, n.Tile.elevation, n.Tile.temperature));
            PlanetTile seaTile = neighbors.FirstOrDefault(n => !Find.WorldObjects.AnyWorldObjectAt(n));
            ctx.Require(seaTile.Valid, "a free neighbouring tile");
            seaTile.Tile.PrimaryBiome = BiomeDefOf.Ocean;
            seaTile.Tile.elevation = -500f;
            seaTile.Tile.temperature = 15f;
            Find.World.tileTemperatures.ClearCaches();
            SurfaceRouting.ClearCache();
            OverboardHazard.debugSeaFactor = 1f;

            Map viewing = Find.CurrentMap;
            Pawn sailor = null;
            List<Thing> spawned = new List<Thing>();
            List<MapParent> sites = new List<MapParent>();
            try
            {
                Map sea = DebugActions_SeaSurface.CreateAt(seaTile);
                ctx.Require(sea != null, "a sea-surface site next to the colony");
                sites.Add(sea.Parent);
                Current.Game.CurrentMap = sea;
                CellRect deck = CellRect.CenteredOn(sea.Center, 4).ExpandedBy(0);
                deck = new CellRect(deck.minX, deck.minZ, 9, 7); // 非方形：轉 90° 外框會變
                foreach (IntVec3 c in deck) { sea.terrainGrid.SetTerrain(c, platform); }
                foreach (IntVec3 c in deck)
                {
                    Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                    conduit.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(conduit, c, sea);
                }
                Thing Build(ThingDef def, IntVec3 at, Rot4 rot)
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                    t.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(t, at, sea, rot);
                    spawned.Add(t);
                    return t;
                }
                IntVec3 center = deck.CenterCell;
                Thing keel = Build(ThingDef.Named("DepthRim_Keel"), center, Rot4.North);
                Thing helm = Build(ThingDef.Named("DepthRim_Helm"), center + new IntVec3(-2, 0, 2), Rot4.North);
                Thing thruster = Build(DepthRimDefOf.DepthRim_SurfaceThruster, center + new IntVec3(3, 0, -1), Rot4.North);
                Thing battery = Build(ThingDef.Named("Battery"), center + new IntVec3(-3, 0, -2), Rot4.North);
                Thing cargo = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), center + new IntVec3(1, 0, -2), sea);
                cargo.stackCount = 31;
                spawned.Add(cargo);
                Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, sea.zoneManager);
                sea.zoneManager.RegisterZone(zone);
                // 甲板上沒有建築的 2×2（龍骨、艦橋、推進器、電池之外）
                foreach (IntVec3 c in CellRect.FromLimits(deck.minX + 5, deck.minZ + 5, deck.minX + 6, deck.minZ + 6)) { zone.AddCell(c); }
                List<IntVec3> zoneBefore = zone.Cells.ToList();
                sailor = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(sailor, center + new IntVec3(0, 0, -2), sea);
                if (sailor.drafter != null) { sailor.drafter.Drafted = true; }
                battery.TryGetComp<CompPowerBattery>().AddEnergy(5000f);
                MapComponent_PileNetwork.For(sea).MarkDirty();
                CompPowerTrader helmPower = helm.TryGetComp<CompPowerTrader>();
                yield return new WaitUntil(() => helmPower.PowerOn && thruster.TryGetComp<CompPowerTrader>().PowerOn, 1200);

                ctx.Check(Docking.AdjacentColony(seaTile) == home, "the colony is next to the ship's tile");
                string block = Docking.BlockReason(helm, out SurfaceVoyage.Plan plan, out Map colony);
                ctx.Require(block == null, $"the ship can dock ({block})");
                ctx.Check(plan.footprint.Count == deck.Area, $"the whole deck is the hull ({plan.footprint.Count}/{deck.Area})");
                ctx.Check(SubmarineNavigation.SurfacedOptions(helm).Any(o => o.Label == "DepthRim.Dock.Option".Translate(home.Parent.LabelCap).ToString()), "the helm's menu offers docking at the colony");

                // ── 泊位檢查 ──
                Designator_DockShip designator = new Designator_DockShip(helm, home, plan.footprint.ToList());
                // 非方形又不能轉的建築：模組裡的排水泵是 1×1，借電池（1×2）暫時當成不能轉的，檢查完馬上還原
                bool batteryRotatable = battery.def.rotatable;
                try
                {
                    battery.def.rotatable = false;
                    AcceptanceReport blocked = designator.Evaluate(berthCenter, 1);
                    ctx.Check(!blocked.Accepted && blocked.Reason.Contains(battery.LabelCap), $"a non-rotatable {battery.def.size.x}x{battery.def.size.z} building blocks a quarter turn ({blocked.Reason})");
                    ctx.Check(designator.Evaluate(berthCenter, 2).Accepted, "but not a half turn");
                }
                finally
                {
                    battery.def.rotatable = batteryRotatable;
                }
                IntVec3 land = bay.CenterCell + inward * (BayD / 2 + 6);
                ctx.Check(!designator.Evaluate(land, 1).Accepted, "a berth on land is refused");
                AcceptanceReport ok = designator.Evaluate(berthCenter, 1);
                ctx.Require(ok.Accepted, $"a berth inside the bay is accepted ({ok.Reason})");
                List<IntVec3> berthCells = designator.BerthCells(berthCenter, 1);
                CellRect berthBox = CellRect.FromCellList(berthCells);
                ctx.Check(berthBox.Width == 7 && berthBox.Height == 9, $"a quarter turn swaps the outline to 7x9 ({berthBox.Width}x{berthBox.Height})");

                // ── 靠岸 ──
                HarborCutscene.Last = null;
                ctx.Require(Docking.Dock(helm, 1, berthCenter), "docking started");
                if (show)
                {
                    foreach (Wait w in FramesUntil(() => ShowOpen() && HarborCutscene.Last.CapturedTexture != null && HarborCutscene.Last.Progress > 0.02f, 900)) { yield return w; }
                    HarborCutscene showing = HarborCutscene.Last;
                    ctx.Require(showing != null && ShowOpen(), "the docking show is playing");
                    ctx.Check(!showing.Departing && !showing.ShortVersion, "it is the full docking show");
                    ctx.Check(helm.Map != home, "during the show the real hull is not in the colony yet");
                    Vector3Ish(showing.CurrentPose, berthCells, out float startDist);
                    ctx.Check(startDist > 8f, $"the ship starts out on the approach route, away from the berth ({startDist:0.0} cells)");
                    foreach (Wait w in FramesUntil(() => !ShowOpen() || showing.Progress >= 0.4f, 1200)) { yield return w; }
                    ctx.Screenshot("dock_show_approach"); yield return new WaitFrames(3);
                    foreach (Wait w in FramesUntil(() => !ShowOpen() || showing.Progress >= 0.78f, 1200)) { yield return w; }
                    if (ShowOpen())
                    {
                        Vector3Ish(showing.CurrentPose, berthCells, out float slideDist);
                        ctx.Check(slideDist < 6f && System.Math.Abs(showing.CurrentPose.angle) < 1f, $"near the end it slides into the berth already turned ({slideDist:0.0} cells, {showing.CurrentPose.angle:0}°)");
                        ctx.Note($"berth style: {(showing.SideBerth ? "side berth" : "anchorage")}");
                    }
                    ctx.Screenshot("dock_show_slide"); yield return new WaitFrames(3);
                    foreach (Wait w in FramesUntil(() => !ShowOpen(), 1200)) { yield return w; }
                    ctx.Check(!ShowOpen(), "the show ends on its own");
                }
                yield return new WaitUntil(() => helm.Map == home, 600);
                ctx.Require(helm.Map == home, "the helm is in the colony");
                HashSet<IntVec3> berthSet = new HashSet<IntVec3>(berthCells);
                List<IntVec3> hullOnHome = home.AllCells.Where(c => MapComponent_SeabedMemory.IsPlatform(home.terrainGrid.TerrainAt(c))).ToList();
                ctx.Check(hullOnHome.Count == deck.Area && hullOnHome.All(berthSet.Contains), $"the hull lies exactly on the chosen berth ({hullOnHome.Count} cells)");
                ctx.Check(helm.Rotation == Rot4.East, $"the helm turned with the ship ({helm.Rotation})");
                ctx.Check(new[] { keel, thruster, battery, cargo }.All(t => t.Spawned && t.Map == home && berthSet.Contains(t.Position)), "keel, thruster, battery and cargo are aboard at the berth");
                ctx.Check(sailor.Spawned && sailor.Map == home && berthSet.Contains(sailor.Position), "the colonist came along");
                Zone movedZone = home.zoneManager.AllZones.OfType<Zone_Stockpile>().FirstOrDefault(z => z.Cells.Any(berthSet.Contains));
                ctx.Check(movedZone != null && movedZone.Cells.Count == zoneBefore.Count && movedZone.Cells.All(berthSet.Contains), $"the stockpile on deck came along, turned ({movedZone?.Cells.Count ?? 0} cells)");
                PileNetwork net = MapComponent_PileNetwork.For(home)?.NetworkContaining(helm);
                net?.RecalculateFootprint();
                ctx.Check(net != null && net.Footprint.Count == deck.Area, $"the keel network is whole after turning ({net?.Footprint.Count ?? 0})");
                MapComponent_Berths.Berth berth = home.GetComponent<MapComponent_Berths>()?.For(helm.TryGetComp<CompTransferConsole>()?.ShipName);
                ctx.Check(berth != null && berth.turns == 1 && berth.seaTileId == seaTile.tileId, "the berth is remembered, with the sea tile the ship came from");
                ctx.Check(Find.LetterStack.LettersListForReading.Any(l => l.Label == "DepthRim.Dock.LetterLabel".Translate()), "a letter reports the docking");
                yield return new WaitUntil(() => !Find.WorldObjects.AllWorldObjects.OfType<WorldObject_SeaSurfaceSite>().Any(s => s.Tile == seaTile), 600);
                ctx.Check(!Find.WorldObjects.AllWorldObjects.OfType<WorldObject_SeaSurfaceSite>().Any(s => s.Tile == seaTile), "the emptied sea site and the temporary ship map go away on their own");
                foreach (Wait w in ctx.ScreenshotOn(home, berthCenter, 18f, "docked")) { yield return w; }
                yield return new WaitFrames(30);

                // ── 再下水：從記下的那一格出去 ──
                string launchBlock = CoastalLaunch.BlockReason(helm, out _, out PlanetTile target);
                ctx.Check(launchBlock == null && target == seaTile, $"launching again goes out by the remembered tile ({launchBlock}, {target})");
                if (launchBlock == null)
                {
                    CoastalLaunch.quiet = !show;
                    HarborCutscene.Last = null;
                    CoastalLaunch.Launch(helm);
                    yield return new WaitUntil(() => helm.Map != home, 600);
                    ctx.Check(helm.Map != home && berthCells.All(c => home.terrainGrid.TerrainAt(c) == deep), "the ship left again and the bay is water again");
                    if (show)
                    {
                        foreach (Wait w in FramesUntil(() => ShowOpen() && HarborCutscene.Last.CapturedTexture != null && HarborCutscene.Last.Progress > 0.3f, 1200)) { yield return w; }
                        HarborCutscene leaving = HarborCutscene.Last;
                        ctx.Check(leaving != null && leaving.Departing, "launching plays the leaving show");
                        if (leaving != null)
                        {
                            ctx.Screenshot("launch_show"); yield return new WaitFrames(3);
                        }
                        foreach (Wait w in FramesUntil(() => !ShowOpen(), 1500)) { yield return w; }
                        ctx.Check(!ShowOpen(), "the leaving show ends on its own");

                        // 同一個泊位再靠一次：短版
                        yield return new WaitTicks(5);
                        string again = Docking.BlockReason(helm, out _, out _);
                        ctx.Check(again == null, $"the ship can dock again ({again})");
                        if (again == null)
                        {
                            HarborCutscene.Last = null;
                            Docking.Dock(helm, 1, berthCenter);
                            foreach (Wait w in FramesUntil(() => ShowOpen(), 900)) { yield return w; }
                            ctx.Check(HarborCutscene.Last != null && HarborCutscene.Last.ShortVersion, "docking again at the same berth plays the short show");
                            foreach (Wait w in FramesUntil(() => !ShowOpen(), 900)) { yield return w; }
                            yield return new WaitUntil(() => helm.Map == home, 600);
                            ctx.Check(helm.Map == home, "and the ship is back at its berth");
                        }
                    }
                }
            }
            finally
            {
                SubmarineSurfacing.skipCutscenes = skipBefore;
                AcceptanceDriver.KeepWindowOpen = keepBefore;
                CoastalLaunch.quiet = false;
                OverboardHazard.debugSeaFactor = null;
                Current.Game.CurrentMap = viewing;
                if (sailor != null && !sailor.Destroyed) { if (sailor.Spawned) { sailor.DeSpawn(); } sailor.Destroy(); }
                foreach (Thing t in spawned.Where(t => !t.Destroyed)) { t.Destroy(); }
                foreach (MapParent p in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_SeaSurfaceSite>().Where(s => s.Tile == seaTile).Cast<MapParent>().Concat(sites).Distinct().ToList())
                {
                    if (p.HasMap) { Current.Game.DeinitAndRemoveMap(p.Map, false); }
                    if (!p.Destroyed) { p.Destroy(); }
                }
                foreach (Zone z in home.zoneManager.AllZones.Where(z => z.Cells.Any(bay.Contains)).ToList()) { z.Delete(); }
                foreach (Thing t in bay.Cells.SelectMany(c => c.GetThingList(home)).Where(t => !(t is Pawn)).Distinct().ToList()) { t.Destroy(); }
                foreach (KeyValuePair<IntVec3, TerrainDef> kv in original)
                {
                    if (home.terrainGrid.TerrainAt(kv.Key) != kv.Value) { home.terrainGrid.SetTerrain(kv.Key, kv.Value); }
                }
                foreach (KeyValuePair<PlanetTile, (BiomeDef biome, float elevation, float temperature)> kv in tileBefore)
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
