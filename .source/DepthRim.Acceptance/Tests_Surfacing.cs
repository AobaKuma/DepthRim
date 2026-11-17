using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>上浮與破冰上浮（SubmarineSurfacing、IceBreach/）：用預製潛艇分離出一艘潛艇，移到測試用的海洋格再上浮、下潛。</summary>
    public static class Tests_Surfacing
    {
        internal sealed class Holder
        {
            public WorldObject_Submarine sub;
        }

        /// <summary>在冰洋地圖生成預製潛艇並以開發者分離出航，移到 <paramref name="tile"/>、停在淺海。</summary>
        private static IEnumerable<Wait> LaunchPrefab(TestContext ctx, PlanetTile tile, Holder holder)
        {
            Map ocean = DepthRimFixture.EnsureOcean();
            CellRect hull = DepthRimFixture.SpawnPrefabSubmarine(out IntVec3 center);
            ctx.Require(hull.Area > 0, "spawned the prefab submarine");
            MapComponent_PileNetwork.For(ocean).MarkDirty();
            yield return new WaitTicks(5);
            Building helm = hull.Cells.SelectMany(c => c.GetThingList(ocean)).OfType<Building>().First(b => b.def.defName == "DepthRim_Helm");
            CompTransferConsole console = helm.TryGetComp<CompTransferConsole>();
            HashSet<WorldObject_Submarine> before = new HashSet<WorldObject_Submarine>(Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>());
            ShipSeparationUtility.DebugSeparateNow(console);
            yield return new WaitUntil(() => (holder.sub = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>().FirstOrDefault(s => !before.Contains(s) && s.HasMap)) != null, 600);
            ctx.Require(holder.sub != null, "the prefab cast off as a submarine");
            yield return new WaitRealtime(() => !Find.WindowStack.Windows.Any(w => w is TransferTakeoffCutscene), 30f);
            holder.sub.Tile = tile;
            holder.sub.InitDepth(DepthTier.Shallow);
            ctx.Note($"submarine {holder.sub.Label} moved to {tile} ({tile.Tile.PrimaryBiome.defName}), above {OceanWorldView.TileAbove(tile)} ({OceanWorldView.TileAbove(tile).Tile.PrimaryBiome.defName})");
        }

        /// <summary>一個能航行、上下都沒有世界物件的海洋格；<paramref name="above"/> 篩選正上方的地表格。</summary>
        internal static PlanetTile FindTile(System.Func<Tile, bool> above)
        {
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            PlanetTile home = OceanWorldView.TileBelow(DepthRimFixture.Home.Tile);
            for (int i = 0; i < ocean.TilesCount; i++)
            {
                PlanetTile t = new PlanetTile(i, ocean);
                if (t == home || !OceanRouting.Navigable(t) || Find.WorldObjects.AnyWorldObjectAt(t))
                {
                    continue;
                }
                PlanetTile up = OceanWorldView.TileAbove(t);
                if (up.Valid && up.Tile.WaterCovered && !Find.WorldObjects.AnyWorldObjectAt(up) && above(up.Tile))
                {
                    return t;
                }
            }
            return PlanetTile.Invalid;
        }

        internal static IntVec3 RoofedHullCell(Map map, IEnumerable<IntVec3> hull)
        {
            return hull.FirstOrDefault(c => c.Roofed(map) && c.Standable(map) && c.GetFirstPawn(map) == null && c.GetDoor(map) == null);
        }

        // ==================================================================
        //  U1：常規上浮與下潛
        // ==================================================================

        /// <summary>
        /// U1：潛艇停在淺海、正上方是開放海面：(1) 不在淺海、航行中時上浮停用；(2) 上浮後地表那一格有海面地點、船體平台浮在開放海面上、
        /// 船員跟著上來、潛艇世界物件移除、水位系統不作用；(3) 從艦橋下潛：回到正下方的海洋格（淺海、停著），船體完整，海面留下開放海面，海面地點移除。
        /// </summary>
        public static IEnumerable<Wait> U1_Surfacing(TestContext ctx)
        {
            SubmarineSurfacing.skipCutscenes = true;
            PlanetTile tile = FindTile(t => t.temperature > 5f);
            ctx.Require(tile.Valid, "found an ocean tile under open, ice-free sea");
            PlanetTile above = OceanWorldView.TileAbove(tile);
            Holder holder = new Holder();
            foreach (Wait w in LaunchPrefab(ctx, tile, holder)) { yield return w; }
            WorldObject_Submarine sub = holder.sub;
            Map shipMap = sub.Map;
            int hullCount = TransferLandingUtility.HullCells(shipMap).Count;
            IntVec3 crewCell = RoofedHullCell(shipMap, TransferLandingUtility.HullCells(shipMap));
            Pawn crew = DepthRimFixture.SpawnColonist(shipMap, crewCell);

            // ── 條件 ──
            ctx.Check(IceConditions.At(above).OpenSea, $"the sea above is open ({IceConditions.At(above).kind})");
            ctx.Check(SubmarineSurfacing.SurfaceBlockReason(sub, out _) == null, $"surfacing is allowed ({SubmarineSurfacing.SurfaceBlockReason(sub, out _)})");
            sub.InitDepth(DepthTier.Thermocline);
            ctx.Check(SubmarineSurfacing.SurfaceBlockReason(sub, out _) != null, "surfacing is blocked below the shallows");
            sub.InitDepth(DepthTier.Shallow);
            ctx.Check(SubmarineSurfacing.IceBreachBlockReason(sub, out _, out _) != null, "ice breaking is unavailable (no frame, no ice)");
            ctx.Check(SubmarineNavigation.OwnTileOptions(sub).Any(o => o.Label == "DepthRim.Surface.Label".Translate() && !o.Disabled), "navigating on its own tile offers an enabled Surface option");

            // ── 上浮 ──
            SubmarineSurfacing.Surface(sub);
            yield return new WaitUntil(() => sub.Destroyed, 600);
            ctx.Require(sub.Destroyed, "the submarine world object is gone after surfacing");
            yield return new WaitFrames(5);
            MapParent site = Find.WorldObjects.MapParentAt(above);
            ctx.Require(site is WorldObject_SeaSurfaceSite && site.HasMap, $"a sea-surface site holds the hull ({site?.GetType().Name})");
            Map sea = site.Map;
            List<IntVec3> hull = sea.AllCells.Where(c => c.GetTerrain(sea).defName == "DepthRim_HullPlatform").ToList();
            ctx.Check(hull.Count == hullCount, $"the whole hull came up ({hull.Count}/{hullCount})");
            List<IntVec3> ring = MapComponent_IceSheet.RingAround(sea, hull);
            ctx.Check(ring.All(c => SeaSurface.IsOpenSea(c, sea)), "the hull floats on open sea");
            ctx.Check(crew.Spawned && crew.Map == sea && hull.Contains(crew.Position), "the crew came up with it");
            ctx.Check(!sea.Flooding().Active, "the flooding system is off on the surface");
            ctx.Check(Find.CurrentMap == sea, "the view follows the hull");
            Building helm = sea.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def.defName == "DepthRim_Helm");
            ctx.Require(helm != null, "the helm is on the surface map");
            ctx.Check(helm.GetGizmos().OfType<Command_Action>().Any(g => g.icon == SubmarineNavigation.NavigateIcon && !g.Disabled), "the helm shows an enabled Navigate command");
            ctx.Check(SubmarineNavigation.SurfacedOptions(helm).Any(o => o.Label == "DepthRim.Nav.DiveTo".Translate(DepthTier.Shallow.Label()) && !o.Disabled), "navigating from the surface offers diving to the shallows");
            foreach (Wait w in ctx.ScreenshotOn(sea, CellRect.FromCellList(hull).CenterCell, 26f, "surfaced_open_sea")) { yield return w; }

            // ── 下潛 ──
            string block = SubmarineSurfacing.DiveBlockReason(helm, out _, out PlanetTile below);
            ctx.Require(block == null, $"diving is allowed ({block})");
            HashSet<WorldObject_Submarine> subsBefore = new HashSet<WorldObject_Submarine>(Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>());
            SubmarineSurfacing.Dive(helm);
            WorldObject_Submarine dived = null;
            yield return new WaitUntil(() => (dived = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>().FirstOrDefault(s => !subsBefore.Contains(s) && s.HasMap)) != null, 600);
            ctx.Require(dived != null, "diving made a submarine again");
            ctx.Check(dived.Tile == below && below.LayerDef == DepthRimDefOf.DepthRim_Ocean, $"it is in the ocean below ({dived.Tile})");
            ctx.Check(dived.cruiseTier == DepthTier.Shallow && dived.targetTier == DepthTier.Shallow && !dived.InFlight, "resting in the shallows");
            ctx.Check(TransferLandingUtility.HullCells(dived.Map).Count == hullCount, $"the hull is whole ({TransferLandingUtility.HullCells(dived.Map).Count}/{hullCount})");
            ctx.Check(crew.Map == dived.Map, "the crew went down with it");
            if (!site.Destroyed && site.HasMap)
            {
                ctx.Check(hull.All(c => SeaSurface.IsOpenSea(c, sea)), "open sea is left where the hull was");
            }
            yield return new WaitUntil(() => site.Destroyed, 2000);
            ctx.Check(site.Destroyed, "the empty sea-surface site is removed");

            DepthRimFixture.CleanupAfterTest();
            if (!dived.Destroyed) { dived.Destroy(); }
            SubmarineSurfacing.skipCutscenes = false;
        }

        // ==================================================================
        //  U3：海面的畫法和原版深海比較
        // ==================================================================

        /// <summary>
        /// U3：海面地圖的左半邊鋪原版深海水（WaterOceanDeep），右半邊是 DepthRim 的海面（SectionLayer_SeaSurface），
        /// 在交界處截圖比較顏色與材質（離線用截圖的像素比較兩邊的平均色）。材質要拿到原版深海的光照色階與漣漪貼圖。
        /// </summary>
        public static IEnumerable<Wait> U3_SeaSurfaceLook(TestContext ctx)
        {
            PlanetTile tile = FindTile(t => t.temperature > 5f);
            ctx.Require(tile.Valid, "found an ocean tile");
            PlanetTile above = OceanWorldView.TileAbove(tile);
            Map sea = DebugActions_SeaSurface.CreateAt(above);
            ctx.Require(sea != null && SeaSurface.IsSeaSurfaceMap(sea), "created a sea-surface map");
            UnityEngine.Material mat = sea.GetComponent<MapComponent_SeaSurface>()?.Material;
            ctx.Require(mat != null, "the sea-surface material exists");
            ctx.Check(mat.GetTexture("_RampTex") == MapComponent_SeaSurface.VanillaDeepRamp && MapComponent_SeaSurface.VanillaDeepRamp != null, "the sea uses vanilla deep ocean's lighting ramp");
            ctx.Check(mat.GetTexture("_RippleTex") == TexGame.RippleTex, "the sea uses vanilla ripples");
            TerrainDef deep = DefDatabase<TerrainDef>.GetNamed("WaterOceanDeep");
            IntVec3 c0 = sea.Center;
            foreach (IntVec3 c in CellRect.FromLimits(c0.x - 40, c0.z - 30, c0.x - 1, c0.z + 30).ClipInsideMap(sea))
            {
                sea.terrainGrid.SetTerrain(c, deep);
            }
            Current.Game.CurrentMap = sea;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            yield return new WaitTicks(120);
            foreach (Wait w in ctx.ScreenshotOn(sea, c0, 20f, "vanilla_left_depthrim_right", 60)) { yield return w; }
            Current.Game.CurrentMap = DepthRimFixture.Home;
            yield return new WaitFrames(5);
            MapParent site = Find.WorldObjects.MapParentAt(above);
            if (site != null && !site.Destroyed)
            {
                Current.Game.DeinitAndRemoveMap(sea, false);
                site.Destroy();
            }
        }

        // ==================================================================
        //  U2、I2：出水演出
        // ==================================================================

        /// <summary>
        /// 播出水演出，演出進行到 <paramref name="shots"/> 的每個秒數時截一張（不動鏡頭，拍的就是演出畫面）；
        /// <paramref name="atSecond"/> 在每張之前呼叫（檢查當下的地形）。演出結束後等船體搬上來。
        /// </summary>
        internal static IEnumerable<Wait> WatchCutscene(TestContext ctx, string name, float[] shots, System.Action<SubmarineSurfacingCutscene, float> atSecond)
        {
            AcceptanceDriver.KeepWindowOpen = typeof(SubmarineSurfacingCutscene);
            SubmarineSurfacingCutscene scene = null;
            yield return new WaitRealtime(() => (scene = Find.WindowStack.WindowOfType<SubmarineSurfacingCutscene>()) != null, 20f);
            ctx.Require(scene != null, "the surfacing cutscene opened");
            yield return new WaitRealtime(() => scene.Seconds > 0f || !scene.IsOpen, 20f);
            ctx.Check(scene.IsOpen && scene.Seconds > 0f, "the hull was captured and the cutscene is running");
            ctx.Check(Find.TickManager.Paused, "the game is paused during the cutscene");
            foreach (float s in shots)
            {
                yield return new WaitRealtime(() => scene.Seconds >= s || !scene.IsOpen, 30f);
                if (!scene.IsOpen)
                {
                    break;
                }
                atSecond?.Invoke(scene, scene.Seconds);
                ctx.Screenshot($"{name}_{s:0.0}s");
                yield return new WaitFrames(4);
            }
            yield return new WaitRealtime(() => !scene.IsOpen, 30f);
            ctx.Check(!scene.IsOpen, "the cutscene finished");
            AcceptanceDriver.KeepWindowOpen = null;
        }

        /// <summary>U2：開放海面的出水演出：水下暗影上升、破水、浮出、輕晃；演出結束時船體才搬到海面地圖。</summary>
        public static IEnumerable<Wait> U2_SurfacingCutscene(TestContext ctx)
        {
            SubmarineSurfacing.skipCutscenes = false;
            bool prefBefore = Prefs.GravshipCutscenes;
            Prefs.GravshipCutscenes = true;
            PlanetTile tile = FindTile(t => t.temperature > 5f);
            ctx.Require(tile.Valid, "found an ocean tile under open sea");
            PlanetTile above = OceanWorldView.TileAbove(tile);
            Holder holder = new Holder();
            foreach (Wait w in LaunchPrefab(ctx, tile, holder)) { yield return w; }
            WorldObject_Submarine sub = holder.sub;
            int hullCount = TransferLandingUtility.HullCells(sub.Map).Count;
            AcceptanceDriver.KeepWindowOpen = typeof(SubmarineSurfacingCutscene);
            SubmarineSurfacing.Surface(sub);
            ctx.Check(!sub.Destroyed, "the hull waits for the cutscene before it moves");
            int splashes = 0, churns = 0, split = 0, sliceCount = 0;
            List<float> order = null;
            void Splash(SubmarineSurfacingCutscene scene, float s)
            {
                ctx.Note($"{s:0.0} s: splashes {scene.Splashes}, churn {scene.Churns}, split {scene.SlicesSplit}/{scene.SliceCount}");
                splashes = scene.Splashes;
                churns = scene.Churns;
                split = scene.SlicesSplit;
                sliceCount = scene.SliceCount;
                order = scene.EmergeOrder;
            }
            foreach (Wait w in WatchCutscene(ctx, "open_sea", new[] { 1.2f, 2.7f, 3.0f, 3.3f, 3.8f, 5.5f }, Splash)) { yield return w; }
            ctx.Check(sliceCount > 0 && split == sliceCount, $"the sea is split from bow to stern ({split}/{sliceCount})");
            int third = (order?.Count ?? 0) / 3;
            float head = third > 0 ? order.Take(third).Average() : 0f, tail = third > 0 ? order.Skip(order.Count - third).Average() : 0f;
            ctx.Note($"emerges {order?.Count ?? 0}: first third at {head:0.00}, last third at {tail:0.00} of the way from bow to stern");
            ctx.Check(third > 3 && tail - head > 0.4f, $"the splashes run from the bow to the stern ({head:0.00} -> {tail:0.00})");
            ctx.Check(splashes > 100, $"vanilla emerge-from-water splashes burst around the hull ({splashes})");
            ctx.Check(churns > 100, $"vanilla water churn around the hull ({churns})");
            yield return new WaitUntil(() => sub.Destroyed, 600);
            ctx.Check(sub.Destroyed, "after the cutscene the hull is on the surface");
            MapParent site = Find.WorldObjects.MapParentAt(above);
            ctx.Check(site != null && site.HasMap && site.Map.AllCells.Count(c => c.GetTerrain(site.Map).defName == "DepthRim_HullPlatform") == hullCount, "the whole hull floats on the sea-surface map");

            Prefs.GravshipCutscenes = prefBefore;
            DepthRimFixture.CleanupAfterTest();
            if (site != null && !site.Destroyed) { site.Destroy(); }
        }

        /// <summary>
        /// I2：破冰的出水演出（破冰出水設計稿 §5、§6）：裂紋在破裂前就從接觸點長出、破裂前沒有開放水；破裂瞬間碎冰、開放水一起出現、碎冰塊四射；
        /// 演出結束時船體才搬到冰面上，圖樣全部套上。在 0.5、2.5、3.6、6、9 秒各拍一張。
        /// </summary>
        public static IEnumerable<Wait> I2_IceBreachCutscene(TestContext ctx)
        {
            SubmarineSurfacing.skipCutscenes = false;
            bool prefBefore = Prefs.GravshipCutscenes;
            Prefs.GravshipCutscenes = true;
            PlanetTile tile = FindTile(t => true);
            ctx.Require(tile.Valid, "found an ocean tile under the sea");
            PlanetTile above = OceanWorldView.TileAbove(tile);
            BiomeDef biomeBefore = above.Tile.PrimaryBiome;
            float tempBefore = above.Tile.temperature;
            above.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("SeaIce");
            above.Tile.temperature = -25f;
            Find.World.tileTemperatures.ClearCaches();
            Holder holder = new Holder();
            foreach (Wait w in LaunchPrefab(ctx, tile, holder)) { yield return w; }
            WorldObject_Submarine sub = holder.sub;
            Map shipMap = sub.Map;
            List<IntVec3> shipHull = TransferLandingUtility.HullCells(shipMap);
            HashSet<IntVec3> hullSet = new HashSet<IntVec3>(shipHull);
            ThingDef frameDef = DefDatabase<ThingDef>.GetNamed("DepthRim_IceBreakerFrame");
            IntVec3 frameAt = shipHull.FirstOrDefault(c => GenAdj.OccupiedRect(c, Rot4.North, frameDef.Size).Cells
                .All(x => hullSet.Contains(x) && x.Roofed(shipMap) && x.Standable(shipMap) && x.GetEdifice(shipMap) == null && x.GetFirstItem(shipMap) == null && x.GetFirstPawn(shipMap) == null));
            DepthRimFixture.Spawn(frameDef, frameAt, shipMap);
            // 冰厚：一次就猛烈出水（看得到最大的演出）
            IceConditions.Info ice = IceConditions.At(above);
            float violentH = -1f;
            for (float h = 1.5f; h >= 0.2f && violentH < 0f; h -= 0.02f)
            {
                if (IceBreachMath.Evaluate(h, ice.airTemperature, shipHull.Count, false, 2f, 1).result == IceBreachResult.Violent)
                {
                    violentH = h;
                }
            }
            ctx.Require(violentH > 0f, "found an ice thickness for a violent breakthrough");
            IceConditions.debugThickness = violentH;
            ctx.Note($"test ice {violentH:0.00} m on a {shipHull.Count}-cell hull");
            // 演出在懸停結束時打開：先叫測試驅動程式不要關掉它
            AcceptanceDriver.KeepWindowOpen = typeof(SubmarineSurfacingCutscene);
            SubmarineSurfacing.BeginIceBreach(sub);
            yield return new WaitUntil(() => sub.iceBreachAtTick < 0, SubmarineSurfacing.HoverTicks + 300);
            ctx.Check(!sub.Destroyed, "the hull waits for the cutscene before it moves");

            // 多年冰照原版海冰生成，地圖上本來就有水坑與海水（開放水）：以演出還沒套任何變化時的開放水為基準
            int beforeBreakCracks = -1, beforeBreakWater = -1, afterBreakWater = -1, flying = 0, naturalWater = -1;
            Map iceMap = null;
            void Count(SubmarineSurfacingCutscene scene, float s)
            {
                iceMap = scene.Show.map;
                int cracks = iceMap.AllCells.Count(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_IceCracked);
                int water = iceMap.AllCells.Count(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_BreachWater);
                ctx.Note($"{s:0.0} s: cracks {cracks}, open water {water}, flying ice {scene.FlyingChunks}, changes left {scene.Show.Remaining}/{scene.Show.Total}");
                if (naturalWater < 0 && scene.Show.Remaining == scene.Show.Total)
                {
                    naturalWater = water;
                }
                if (s < IceCrackPattern.BreakTime)
                {
                    beforeBreakCracks = cracks;
                    beforeBreakWater = water;
                }
                else if (afterBreakWater < 0)
                {
                    afterBreakWater = water;
                    flying = scene.FlyingChunks;
                }
            }
            foreach (Wait w in WatchCutscene(ctx, "ice", new[] { 0.5f, 2.5f, 3.6f, 6f, 9f }, Count)) { yield return w; }
            ctx.Check(beforeBreakCracks > 0, $"radial cracks run out before the break ({beforeBreakCracks})");
            ctx.Require(naturalWater >= 0, "saw the ice map before the cutscene changed it");
            ctx.Check(beforeBreakWater == naturalWater, $"no new open water before the break ({beforeBreakWater} vs {naturalWater} natural)");
            ctx.Check(afterBreakWater > naturalWater, $"the break opens the water around the hull ({afterBreakWater} vs {naturalWater} natural)");
            ctx.Check(flying > 50, $"ice chunks fly when it breaks ({flying})");
            yield return new WaitUntil(() => sub.Destroyed, 600);
            ctx.Require(sub.Destroyed && iceMap != null, "after the cutscene the hull is on the ice");
            List<IntVec3> hull = iceMap.AllCells.Where(c => c.GetTerrain(iceMap).defName == "DepthRim_HullPlatform").ToList();
            ctx.Check(hull.Count == shipHull.Count, $"the whole hull is on the ice ({hull.Count}/{shipHull.Count})");
            ctx.Check(MapComponent_IceSheet.RingAround(iceMap, hull).All(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_BreachWater), "open water all around the hull");
            foreach (Wait w in ctx.ScreenshotOn(iceMap, CellRect.FromCellList(hull).CenterCell, 45f, "ice_after")) { yield return w; }

            IceConditions.debugThickness = null;
            above.Tile.PrimaryBiome = biomeBefore;
            above.Tile.temperature = tempBefore;
            Find.World.tileTemperatures.ClearCaches();
            Prefs.GravshipCutscenes = prefBefore;
            DepthRimFixture.CleanupAfterTest();
            MapParent site = Find.WorldObjects.MapParentAt(above);
            if (site != null && !site.Destroyed) { site.Destroy(); }
        }

        // ==================================================================
        //  I1：破冰上浮
        // ==================================================================

        /// <summary>
        /// I1：正上方是海冰（測試把冰厚固定）：(1) 沒有破冰結構、冰比結構上限厚時停用；(2) 懸停一小時後頂冰：冰太厚時部分開裂、結構受損、六小時冷卻；
        /// (3) 一天內再頂（裂過的冰比較好破）就出水：地表那一格有破冰點、船體在冰面上、四周一圈開放水、碎冰、裂紋；
        /// (4) 破口重新結冰：薄冰還能下潛，超過 0.4 m 被凍住，用破冰結構撞開後可以下潛；(5) 下潛後回到海洋層，原地留下開放水重新結冰。
        /// </summary>
        public static IEnumerable<Wait> I1_IceBreach(TestContext ctx)
        {
            SubmarineSurfacing.skipCutscenes = true;
            PlanetTile tile = FindTile(t => true);
            ctx.Require(tile.Valid, "found an ocean tile under the sea");
            PlanetTile above = OceanWorldView.TileAbove(tile);
            BiomeDef biomeBefore = above.Tile.PrimaryBiome;
            float tempBefore = above.Tile.temperature;
            above.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("SeaIce");
            above.Tile.temperature = -25f;
            Find.World.tileTemperatures.ClearCaches();
            Holder holder = new Holder();
            foreach (Wait w in LaunchPrefab(ctx, tile, holder)) { yield return w; }
            WorldObject_Submarine sub = holder.sub;
            Map shipMap = sub.Map;
            List<IntVec3> shipHull = TransferLandingUtility.HullCells(shipMap);
            int hullCount = shipHull.Count;

            IceConditions.Info ice = IceConditions.At(above);
            ctx.Note($"real ice above: {ice.kind} {ice.thickness:0.00} m, air {ice.airTemperature:0} °C, lead {ice.lead}");
            ctx.Check(ice.kind == IceKind.MultiYear && ice.Frozen, "sea ice above is multi-year ice");
            ctx.Check(SubmarineSurfacing.SurfaceBlockReason(sub, out _) != null, "regular surfacing is blocked under ice");
            ctx.Check(SubmarineSurfacing.IceBreachBlockReason(sub, out _, out _) == "DepthRim.IceBreach.NoFrame".Translate(), "no ice-breaker frame: blocked");

            // ── 破冰結構：放在有屋頂的船艙裡 ──
            ThingDef frameDef = DefDatabase<ThingDef>.GetNamed("DepthRim_IceBreakerFrame");
            HashSet<IntVec3> hullSet = new HashSet<IntVec3>(shipHull);
            IntVec3 frameAt = shipHull.FirstOrDefault(c => GenAdj.OccupiedRect(c, Rot4.North, frameDef.Size).Cells
                .All(x => hullSet.Contains(x) && x.Roofed(shipMap) && x.Standable(shipMap) && x.GetEdifice(shipMap) == null && x.GetFirstItem(shipMap) == null && x.GetFirstPawn(shipMap) == null));
            ctx.Require(frameAt.IsValid && frameAt != IntVec3.Zero, "found a roofed 3x3 spot for the frame");
            ctx.Check(new PlaceWorker_IceBreakerFrame().AllowsPlacing(frameDef, frameAt, Rot4.North, shipMap).Accepted, "the frame can be placed under the roof");
            DepthTier ratingBefore = HullPressure.RatingFor(shipMap, out _);
            Thing frame = DepthRimFixture.Spawn(frameDef, frameAt, shipMap);
            ctx.Check(CompIceBreakerFrame.On(shipMap).Count == 1, "the frame is aboard");
            DepthTier ratingAfter = HullPressure.RatingFor(shipMap, out _);
            ctx.Check(ratingAfter == ratingBefore, $"the frame does not change the hull rating ({ratingBefore} → {ratingAfter})");

            // 冰厚：找讓第一次部分開裂、第二次（裂過）出水的厚度
            List<CompIceBreakerFrame> frames = CompIceBreakerFrame.On(shipMap);
            float partialH = -1f, surfaceH = -1f;
            for (float h = 0.2f; h <= 2f && partialH < 0f; h += 0.01f)
            {
                IceBreachOutcome first = IceBreachMath.Evaluate(h, ice.airTemperature, hullCount, false, 2f, 1);
                IceBreachOutcome again = IceBreachMath.Evaluate(h, ice.airTemperature, hullCount, true, 2f, 1);
                if (first.result == IceBreachResult.Partial && again.Success)
                {
                    partialH = h;
                }
            }
            ctx.Require(partialH > 0f, "found an ice thickness that cracks first and breaks the second time");
            IceConditions.debugThickness = partialH;
            ctx.Note($"test ice {partialH:0.00} m on a {hullCount}-cell hull");
            IceConditions.debugThickness = 2.5f;
            ctx.Check(SubmarineSurfacing.IceBreachBlockReason(sub, out _, out IceBreachOutcome tooThick) != null && tooThick.result == IceBreachResult.TooThick, "ice thicker than the frame: blocked");
            IceConditions.debugThickness = partialH;
            ctx.Check(SubmarineSurfacing.IceBreachBlockReason(sub, out _, out IceBreachOutcome est) == null && est.result == IceBreachResult.Partial, $"estimate: partial crack (R {est.ratio:0.00})");
            ctx.Check(SubmarineSurfacing.MeasureReport(sub).Contains(partialH.ToString("0.00")), "the measurement reports the thickness");

            // ── 第一次：部分開裂 ──
            int hpBefore = frame.HitPoints;
            SubmarineSurfacing.BeginIceBreach(sub);
            ctx.Check(sub.iceBreachAtTick > Find.TickManager.TicksGame, "hovering under the ice");
            ctx.Check(SubmarineSurfacing.SurfaceBlockReason(sub, out _) == "DepthRim.IceBreach.Hovering".Translate(), "hovering blocks other surfacing");
            ctx.Check(sub.GetGizmos().OfType<Command_Action>().Any(g => g.defaultLabel == "DepthRim.IceBreach.CancelLabel".Translate()), "hovering can be called off");
            yield return new WaitUntil(() => sub.iceBreachAtTick < 0, SubmarineSurfacing.HoverTicks + 300);
            int now = Find.TickManager.TicksGame;
            ctx.Check(!sub.Destroyed && sub.crackedUntilTick > now, "the ice cracked but held");
            ctx.Check(frame.HitPoints < hpBefore, $"the frame took damage ({hpBefore} → {frame.HitPoints})");
            ctx.Check(sub.nextIceBreachTick > now && SubmarineSurfacing.IceBreachBlockReason(sub, out _, out _) != null, "six hours to recharge before the next try");
            ctx.Check(Find.LetterStack.LettersListForReading.Any(l => l.Label == "DepthRim.IceBreach.PartialLabel".Translate()), "a letter reports the crack");

            // ── 第二次：出水 ──
            yield return new WaitUntil(() => Find.TickManager.TicksGame >= sub.nextIceBreachTick, SubmarineSurfacing.RetryTicks + 300);
            ctx.Check(SubmarineSurfacing.IceBreachBlockReason(sub, out _, out IceBreachOutcome est2) == null && est2.Success, $"cracked ice: now it breaks (R {est2.ratio:0.00})");
            Pawn crew = DepthRimFixture.SpawnColonist(shipMap, RoofedHullCell(shipMap, shipHull));
            SubmarineSurfacing.BeginIceBreach(sub);
            yield return new WaitUntil(() => sub.Destroyed, SubmarineSurfacing.HoverTicks + 600);
            ctx.Require(sub.Destroyed, "the submarine broke through");
            yield return new WaitFrames(5);
            MapParent site = Find.WorldObjects.MapParentAt(above);
            ctx.Require(site is WorldObject_IceBreachSite && site.HasMap, $"an ice breach site holds the hull ({site?.GetType().Name})");
            Map iceMap = site.Map;
            MapComponent_IceSheet sheet = MapComponent_IceSheet.For(iceMap);
            List<IntVec3> hull = iceMap.AllCells.Where(c => c.GetTerrain(iceMap).defName == "DepthRim_HullPlatform").ToList();
            ctx.Check(hull.Count == hullCount, $"the whole hull is on the ice ({hull.Count}/{hullCount})");
            List<IntVec3> ring = MapComponent_IceSheet.RingAround(iceMap, hull);
            ctx.Check(ring.All(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_BreachWater), "open water all around the hull");
            int rubble = iceMap.AllCells.Count(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_IceRubble);
            int cracks = iceMap.AllCells.Count(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_IceCracked);
            int sheetIce = iceMap.AllCells.Count(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_SeaIceSheet);
            ctx.Note($"ice map: rubble {rubble}, cracks {cracks}, sea ice {sheetIce}, refreezing {sheet.RefreezingCount}");
            ctx.Check(rubble > 0 && cracks > 0 && sheetIce > rubble, "rubble and cracks around the hole, sea ice beyond");
            ctx.Check(GenStep_IceFloe.UsesVanillaTerrain(iceMap, (WorldObject_IceBreachSite)site), "multi-year ice: the map follows the vanilla sea ice biome");
            ctx.Check(iceMap.AllCells.All(c => !c.GetTerrain(iceMap).IsWater || c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_BreachWater
                || c.GetTerrain(iceMap).defName == "DepthRim_HullPlatform"), "vanilla water on the ice map became breach water");
            ctx.Check(!iceMap.AllCells.Any(c => c.GetTerrain(iceMap) == TerrainDefOf.Ice), "vanilla ice became sea ice sheet");
            ctx.Check(sheet.RefreezingCount >= ring.Count, "the open water is freezing over");
            ctx.Check(crew.Map == iceMap && hull.Contains(crew.Position), "the crew came up with the hull");
            ctx.Check(Find.LetterStack.LettersListForReading.Any(l => l.Label == "DepthRim.IceBreach.SurfacedLabel".Translate()), "a letter reports the breakthrough");
            foreach (Wait w in ctx.ScreenshotOn(iceMap, CellRect.FromCellList(hull).CenterCell, 34f, "ice_breach")) { yield return w; }

            // ── 重新結冰與被凍住 ──
            Building helm = iceMap.listerBuildings.allBuildingsColonist.First(b => b.def.defName == "DepthRim_Helm");
            ctx.Check(SubmarineSurfacing.DiveBlockReason(helm, out _, out _) == null, "right after surfacing the submarine can dive");
            sheet.Advance(IceBreachMath.FreezingDegreeDays(-20f, 0.5f));
            ctx.Check(ring.All(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_NewIce), "half a day of frost: new ice around the hull");
            ctx.Check(SubmarineSurfacing.DiveBlockReason(helm, out _, out _) == null, $"thin new ice does not stop a dive ({sheet.ThickestAround(hull):0.00} m)");
            sheet.Advance(IceBreachMath.FreezingDegreeDays(-20f, 3.5f));
            ctx.Check(sheet.FrozenIn(hull), $"after four days the hull is frozen in ({sheet.ThickestAround(hull):0.00} m)");
            string frozen = SubmarineSurfacing.DiveBlockReason(helm, out _, out _);
            ctx.Check(frozen != null, $"frozen in: diving is blocked ({frozen})");
            Thing frameOnIce = iceMap.listerBuildings.allBuildingsColonist.First(b => b.def == frameDef);
            int frameHp = frameOnIce.HitPoints;
            ctx.Check(SubmarineSurfacing.BreakFreeBlockReason(helm, out _, out _) == null, "the frame can break the hull free");
            SubmarineSurfacing.BreakFree(helm);
            ctx.Check(!sheet.FrozenIn(hull) && ring.All(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_BreachWater), "the ice around the hull is open water again");
            ctx.Check(frameOnIce.HitPoints < frameHp, "breaking free cost the frame some health");

            // ── 下潛 ──
            HashSet<WorldObject_Submarine> subsBefore = new HashSet<WorldObject_Submarine>(Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>());
            SubmarineSurfacing.Dive(helm);
            WorldObject_Submarine dived = null;
            yield return new WaitUntil(() => (dived = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>().FirstOrDefault(s => !subsBefore.Contains(s) && s.HasMap)) != null, 600);
            ctx.Require(dived != null, "dived back under the ice");
            ctx.Check(dived.cruiseTier == DepthTier.Shallow && TransferLandingUtility.HullCells(dived.Map).Count == hullCount, "a whole submarine resting in the shallows");
            if (!site.Destroyed && site.HasMap)
            {
                ctx.Check(hull.All(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_BreachWater), "the hull's footprint is open water, freezing over");
            }

            IceConditions.debugThickness = null;
            above.Tile.PrimaryBiome = biomeBefore;
            above.Tile.temperature = tempBefore;
            Find.World.tileTemperatures.ClearCaches();
            DepthRimFixture.CleanupAfterTest();
            if (!dived.Destroyed) { dived.Destroy(); }
            if (!site.Destroyed) { site.Destroy(); }
            SubmarineSurfacing.skipCutscenes = false;
        }
    }
}
