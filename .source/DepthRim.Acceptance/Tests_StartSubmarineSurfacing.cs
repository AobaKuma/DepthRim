using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>
    /// 用潛艇開局的潛艇（遊戲內匯出的 <c>DR_Submarine_Test</c>，13×92、近千格船體）實際跑一次上浮與破冰上浮，演出照播。
    /// 只在 run.ps1 -Scenario DepthRim_SubmarineStart -Only G2 時跑。
    /// </summary>
    public static class Tests_StartSubmarineSurfacing
    {
        /// <summary>
        /// G2：新遊戲開局的潛艇（船員在船上）出港 → 移到開放海面下的淺海 → 上浮（演出）→ 下潛 → 移到海冰下 → 裝破冰結構 → 破冰上浮（演出）→ 船體與船員在冰面上。
        /// </summary>
        public static IEnumerable<Wait> G2_StartSubmarineSurfacing(TestContext ctx)
        {
            ScenPart_SubmarineStart part = ScenPart_SubmarineStart.Current;
            ctx.Require(part != null, "the game started with the submarine start");
            Map home = Find.CurrentMap;
            CellRect rect = SubmarinePrefab.Footprint(part.Structure, home.Center, part.rot);
            List<IntVec3> startHull = SubmarinePrefab.HullCells(home, rect);
            ctx.Note($"start submarine {part.Structure.defName}: {startHull.Count} hull cells, footprint {rect.Width}x{rect.Height}");
            Building helm = rect.Cells.SelectMany(c => c.GetThingList(home)).OfType<Building>().FirstOrDefault(b => b.TryGetComp<CompTransferConsole>() != null);
            ctx.Require(helm != null, "the start submarine has a helm");
            // M6：開局的結構沒有壓載時，開局補到全速需要的座數（Ballast.FillHull）
            int tanks = Ballast.TanksIn(home, new HashSet<IntVec3>(startHull));
            ctx.Check(tanks >= Ballast.Needed(startHull.Count), $"the start submarine has enough ballast to dive at full speed ({tanks} for {startHull.Count} cells)");
            List<Pawn> crew = home.mapPawns.FreeColonistsSpawned.ToList();
            ctx.Check(crew.Count == 3 && crew.All(p => startHull.Contains(p.Position)), "the crew is aboard");
            foreach (Pawn p in crew)
            {
                if (p.drafter != null) { p.drafter.Drafted = true; }
            }

            // ── 出港 ──
            bool prefBefore = Prefs.GravshipCutscenes;
            Prefs.GravshipCutscenes = true;
            MapComponent_PileNetwork.For(home).MarkDirty();
            yield return new WaitTicks(5);
            ShipSeparationUtility.DebugSeparateNow(helm.TryGetComp<CompTransferConsole>());
            WorldObject_Submarine sub = null;
            yield return new WaitUntil(() => (sub = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>().FirstOrDefault(s => s.HasMap)) != null, 600);
            ctx.Require(sub != null, "the start submarine cast off");
            yield return new WaitRealtime(() => !Find.WindowStack.Windows.Any(w => w is TransferTakeoffCutscene), 30f);
            int hullCount = TransferLandingUtility.HullCells(sub.Map).Count;
            ctx.Check(hullCount >= startHull.Count, $"the whole hull left the dock ({hullCount} cells)");
            ctx.Check(crew.All(p => p.Map == sub.Map), "the crew is on the submarine");

            // ── 常規上浮（演出）──
            SubmarineSurfacing.skipCutscenes = false;
            PlanetTile openTile = Tests_Surfacing.FindTile(t => t.temperature > 5f);
            ctx.Require(openTile.Valid, "found an ocean tile under open sea");
            sub.Tile = openTile;
            sub.InitDepth(DepthTier.Shallow);
            string block = SubmarineSurfacing.SurfaceBlockReason(sub, out _);
            ctx.Require(block == null, $"the start submarine can surface ({block})");
            AcceptanceDriver.KeepWindowOpen = typeof(SubmarineSurfacingCutscene);
            SubmarineSurfacing.Surface(sub);
            float blackShare = -1f;
            bool soft = false;
            List<float> order = null;
            bool alongX = false;
            int bowSign = 0;
            int midStrips = -1, stripCount = 0;
            Vector3? sternNormal = null;
            // 船頭是舵輪那端（開局潛艇：推進器與細長龍骨在船尾）
            CellRect subRect = CellRect.FromCellList(TransferLandingUtility.HullCells(sub.Map));
            Building subHelm = sub.Map.listerBuildings.allBuildingsColonist.First(b => b.TryGetComp<CompTransferConsole>() != null);
            void Inspect(SubmarineSurfacingCutscene scene, float s)
            {
                soft |= scene.HasSoftSilhouette;
                ctx.Note($"{s:0.0} s: splashes {scene.Splashes}, churn {scene.Churns}, split {scene.SlicesSplit}/{scene.SliceCount}, emerges so far {scene.EmergeOrder.Count} (latest at {(scene.EmergeOrder.Count > 0 ? scene.EmergeOrder.Last() : 0f):0.00} from bow)");
                order = scene.EmergeOrder;
                alongX = scene.AlongX;
                bowSign = scene.BowSign;
                ctx.Note($"{s:0.0} s: hull strips shown {scene.StripsShown}/{scene.StripCount}");
                if (s > 2.9f && s < 3.4f) { midStrips = scene.StripsShown; stripCount = scene.StripCount; }
                if (sternNormal == null && scene.SliceCount > 0)
                {
                    // 船尾細長龍骨的旁邊（離尾端 3 格、緊貼左舷外一格）：法線應該朝旁邊，不是往後
                    HashSet<IntVec3> hull = scene.Show.hull;
                    int sternZ = scene.BowSign > 0 ? hull.Min(c => c.z) : hull.Max(c => c.z);
                    int z = sternZ + (scene.BowSign > 0 ? 3 : -3);
                    int minX = hull.Where(c => c.z == z).Min(c => c.x);
                    sternNormal = scene.HullNormal(new IntVec3(minX - 1, 0, z).ToVector3Shifted());
                }
                if (blackShare >= 0f) { return; }
                // 擷取到的船體影像：不透明的像素裡有多少是全黑的（長船超出畫面的那一截沒畫到時會是一大片黑）
                Texture2D tex = scene.CapturedTexture;
                if (tex == null) { return; }
                Color32[] px = tex.GetPixels32();
                int opaque = 0, black = 0;
                foreach (Color32 p in px)
                {
                    if (p.a < 200) { continue; }
                    opaque++;
                    // 沒畫到的區塊是擷取鏡頭的底色：近黑或均勻的深灰（2026-11-15：船尾那一截是 (25,25,25) 上下，舊的「近黑」門檻抓不到）
                    int max = Mathf.Max(p.r, Mathf.Max(p.g, p.b)), min = Mathf.Min(p.r, Mathf.Min(p.g, p.b));
                    if (max < 45 && max - min <= 3) { black++; }
                }
                blackShare = opaque > 0 ? black / (float)opaque : 1f;
                ctx.Note($"captured hull image {tex.width}x{tex.height}: {opaque} opaque pixels, {blackShare:P1} black");
            }
            foreach (Wait w in Tests_Surfacing.WatchCutscene(ctx, "open_sea", new[] { 1.5f, 2.7f, 3.0f, 3.3f, 3.8f, 6f }, Inspect)) { yield return w; }
            ctx.Check(blackShare >= 0f && blackShare < 0.1f, $"the whole long hull was captured, not cut off in black or flat grey ({blackShare:P1} black)");
            ctx.Check(soft, "the underwater silhouette has a soft edge");
            ctx.Check(stripCount > 0 && midStrips > 0 && midStrips < stripCount, $"mid-sweep the hull is only partly revealed ({midStrips}/{stripCount} strips)");
            ctx.Check(sternNormal.HasValue && Mathf.Abs(sternNormal.Value.x) > Mathf.Abs(sternNormal.Value.z) && sternNormal.Value.x < 0f, $"splashes beside the stern keel fly sideways ({sternNormal})");
            int expectedBow = alongX ? (subHelm.Position.x > subRect.CenterCell.x ? 1 : -1) : (subHelm.Position.z > subRect.CenterCell.z ? 1 : -1);
            ctx.Check(bowSign == expectedBow, $"the splashes start at the bow (helm end): bow sign {bowSign}, expected {expectedBow}");
            int third = (order?.Count ?? 0) / 3;
            float head = third > 0 ? order.Take(third).Average() : 0f, tail = third > 0 ? order.Skip(order.Count - third).Average() : 0f;
            ctx.Check(third > 3 && tail - head > 0.4f, $"the splashes run from the bow to the stern of the start submarine ({head:0.00} -> {tail:0.00} over {order?.Count ?? 0} emerges)");
            yield return new WaitUntil(() => sub.Destroyed, 600);
            ctx.Require(sub.Destroyed, "surfaced");
            Map sea = Find.WorldObjects.MapParentAt(OceanWorldView.TileAbove(openTile))?.Map;
            ctx.Require(sea != null && SeaSurface.IsSeaSurfaceMap(sea), "the hull is on the sea-surface map");
            List<IntVec3> seaHull = sea.AllCells.Where(c => c.GetTerrain(sea).defName == "DepthRim_HullPlatform").ToList();
            ctx.Check(seaHull.Count == hullCount, $"the whole hull floats ({seaHull.Count}/{hullCount})");
            ctx.Check(MapComponent_IceSheet.RingAround(sea, seaHull).All(c => SeaSurface.IsOpenSea(c, sea)), "it floats on open sea");
            ctx.Check(crew.All(p => p.Map == sea && seaHull.Contains(p.Position)), "the crew came up aboard");
            ctx.Check(!Find.GameEnder.gameEnding, "the colony carries on");
            foreach (Wait w in ctx.ScreenshotOn(sea, CellRect.FromCellList(seaHull).CenterCell, 50f, "surfaced")) { yield return w; }

            // ── 下潛 ──
            Building seaHelm = sea.listerBuildings.allBuildingsColonist.First(b => b.TryGetComp<CompTransferConsole>() != null);
            block = SubmarineSurfacing.DiveBlockReason(seaHelm, out _, out _);
            ctx.Require(block == null, $"it can dive ({block})");
            HashSet<WorldObject_Submarine> before = new HashSet<WorldObject_Submarine>(Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>());
            System.Type keepBefore = AcceptanceDriver.KeepWindowOpen;
            AcceptanceDriver.KeepWindowOpen = typeof(SubmarineDiveCutscene);
            SubmarineSurfacing.Dive(seaHelm);
            yield return new WaitUntil(() => (sub = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>().FirstOrDefault(s => !before.Contains(s) && s.HasMap)) != null, 600);
            ctx.Require(sub != null, "dived again");
            // 下潛演出：船體在原地沉下去（不是離港那樣滑走）
            SubmarineDiveCutscene dive = null;
            yield return new WaitRealtime(() => (dive = Find.WindowStack.WindowOfType<SubmarineDiveCutscene>()) != null, 20f);
            ctx.Check(!ModsConfig.OdysseyActive || dive != null, "diving plays the dive cutscene");
            ctx.Check(!Find.WindowStack.IsOpen<SubmarineTakeoffCutscene>(), "not the cast-off cutscene");
            if (dive != null)
            {
                yield return new WaitRealtime(() => dive.Seconds > 0f || !dive.IsOpen, 20f);
                foreach (float s in new[] { 0.8f, 2.5f, 4.5f, 6.8f })
                {
                    yield return new WaitRealtime(() => dive.Seconds >= s || !dive.IsOpen, 30f);
                    if (!dive.IsOpen) { break; }
                    ctx.Note($"{dive.Seconds:0.0} s: vents {dive.Vents}, churn {dive.Churns}, washes {dive.Washes}, ripples {dive.Ripples}");
                    ctx.Screenshot($"dive_{s:0.0}s");
                    yield return new WaitFrames(4);
                }
                ctx.Check(dive.Vents > 10, $"the ballast vents spray along the spine ({dive.Vents})");
                ctx.Check(dive.Churns > 50, $"the sea churns along the sides ({dive.Churns})");
                ctx.Check(dive.Washes > 10, $"water washes over the deck ({dive.Washes})");
                ctx.Check(dive.Ripples > 5, $"the sea closes over the hull in ripples ({dive.Ripples})");
                yield return new WaitRealtime(() => !dive.IsOpen, 30f);
                ctx.Check(!dive.IsOpen, "the dive cutscene finished");
            }
            AcceptanceDriver.KeepWindowOpen = keepBefore;
            ctx.Check(TransferLandingUtility.HullCells(sub.Map).Count == hullCount && crew.All(p => p.Map == sub.Map), "hull and crew went down together");

            // ── 破冰上浮（演出）──
            PlanetTile iceTile = Tests_Surfacing.FindTile(t => true);
            ctx.Require(iceTile.Valid, "found an ocean tile for the ice test");
            PlanetTile above = OceanWorldView.TileAbove(iceTile);
            BiomeDef biomeBefore = above.Tile.PrimaryBiome;
            float tempBefore = above.Tile.temperature;
            above.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("SeaIce");
            above.Tile.temperature = -25f;
            Find.World.tileTemperatures.ClearCaches();
            sub.Tile = iceTile;
            sub.InitDepth(DepthTier.Shallow);
            Map shipMap = sub.Map;
            List<IntVec3> shipHull = TransferLandingUtility.HullCells(shipMap);
            HashSet<IntVec3> hullSet = new HashSet<IntVec3>(shipHull);
            ThingDef frameDef = DefDatabase<ThingDef>.GetNamed("DepthRim_IceBreakerFrame");
            IntVec3 frameAt = shipHull.FirstOrDefault(c => GenAdj.OccupiedRect(c, Rot4.North, frameDef.Size).Cells
                .All(x => hullSet.Contains(x) && x.Roofed(shipMap) && x.Standable(shipMap) && x.GetEdifice(shipMap) == null && x.GetFirstItem(shipMap) == null && x.GetFirstPawn(shipMap) == null));
            ctx.Require(frameAt.IsValid && hullSet.Contains(frameAt), "found a roofed 3x3 spot for an ice-breaker frame");
            Thing frame = GenSpawn.Spawn(ThingMaker.MakeThing(frameDef), frameAt, shipMap);
            frame.SetFaction(Faction.OfPlayer);
            ctx.Note($"ice-breaker frame at {frameAt}");

            // 冰厚：這艘船頂得破的最厚的冰（結構上限 2 m 以內），裂得最開
            IceConditions.Info ice = IceConditions.At(above);
            float h = -1f;
            IceBreachOutcome best = default;
            for (float t = 2f; t >= 0.3f && h < 0f; t -= 0.02f)
            {
                IceBreachOutcome o = IceBreachMath.Evaluate(t, ice.airTemperature, shipHull.Count, false, 2f, 1);
                if (o.Success)
                {
                    h = t;
                    best = o;
                }
            }
            ctx.Require(h > 0f, "found ice this hull can break");
            IceConditions.debugThickness = h;
            ctx.Note($"ice {h:0.00} m, air {ice.airTemperature:0} °C: R {best.ratio:0.00} ({best.result}), l_c {IceBreachMath.CharacteristicLengthCells(h):0.0} cells");
            ctx.Note("measure: " + SubmarineSurfacing.MeasureReport(sub).Replace("\n", " | "));
            block = SubmarineSurfacing.IceBreachBlockReason(sub, out _, out _);
            ctx.Require(block == null, $"the start submarine can break the ice ({block})");
            AcceptanceDriver.KeepWindowOpen = typeof(SubmarineSurfacingCutscene);
            SubmarineSurfacing.BeginIceBreach(sub);
            yield return new WaitUntil(() => sub.iceBreachAtTick < 0, SubmarineSurfacing.HoverTicks + 300);
            Map iceMap = null;
            int fissures = 0, jets = 0, jetsBeforeBreak = 0;
            void Count(SubmarineSurfacingCutscene scene, float s)
            {
                iceMap = scene.Show.map;
                int cracks = iceMap.AllCells.Count(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_IceCracked);
                int water = iceMap.AllCells.Count(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_BreachWater);
                int rubble = iceMap.AllCells.Count(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_IceRubble);
                ctx.Note($"{s:0.0} s: cracks {cracks}, rubble {rubble}, open water {water}, flying ice {scene.FlyingChunks}, fissures {scene.Fissures}, gas jets {scene.GasJets}, reach {scene.Show.reach:0} cells");
                fissures = scene.Fissures;
                jets = scene.GasJets;
                if (s < IceCrackPattern.BreakTime)
                {
                    jetsBeforeBreak = scene.GasJets;
                }
            }
            foreach (Wait w in Tests_Surfacing.WatchCutscene(ctx, "ice", new[] { 0.5f, 1.5f, 2.8f, 3.6f, 4.5f, 6f, 9f }, Count)) { yield return w; }
            ctx.Check(fissures > 100, $"vanilla ground fissures open along the cracks ({fissures})");
            ctx.Check(jetsBeforeBreak > 0 && jets > jetsBeforeBreak + 80, $"gas vents from the cracks, hardest at the break ({jetsBeforeBreak} before, {jets} in all)");
            yield return new WaitUntil(() => sub.Destroyed, 600);
            ctx.Require(sub.Destroyed && iceMap != null, "broke through the ice");
            List<IntVec3> iceHull = iceMap.AllCells.Where(c => c.GetTerrain(iceMap).defName == "DepthRim_HullPlatform").ToList();
            ctx.Check(iceHull.Count == hullCount, $"the whole hull is on the ice ({iceHull.Count}/{hullCount})");
            ctx.Check(MapComponent_IceSheet.RingAround(iceMap, iceHull).All(c => c.GetTerrain(iceMap) == DepthRimDefOf.DepthRim_BreachWater), "open water all around the hull");
            ctx.Check(crew.All(p => p.Map == iceMap && iceHull.Contains(p.Position)), "the crew came up aboard");
            ctx.Check(frame.HitPoints < frame.MaxHitPoints, $"the frame took the blow ({frame.HitPoints}/{frame.MaxHitPoints})");
            ctx.Check(!Find.GameEnder.gameEnding, "the colony carries on");
            CellRect iceRect = CellRect.FromCellList(iceHull);
            foreach (Wait w in ctx.ScreenshotOn(iceMap, iceRect.CenterCell, Mathf.Min(70f, iceRect.Height * 0.75f + 10f), "on_the_ice")) { yield return w; }
            foreach (Wait w in ctx.ScreenshotOn(iceMap, new IntVec3(iceRect.CenterCell.x, 0, iceRect.maxZ - 8), 22f, "bow_close")) { yield return w; }

            IceConditions.debugThickness = null;
            above.Tile.PrimaryBiome = biomeBefore;
            above.Tile.temperature = tempBefore;
            Find.World.tileTemperatures.ClearCaches();
            Prefs.GravshipCutscenes = prefBefore;
            AcceptanceDriver.KeepWindowOpen = null;
        }
    }
}
