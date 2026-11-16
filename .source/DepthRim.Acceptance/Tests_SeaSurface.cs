using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DepthRim.Acceptance
{
    /// <summary>海面的兩層：水面層（海面地圖）與平台層（口袋地圖），由平台支柱連起來。</summary>
    public static class Tests_SeaSurface
    {
        /// <summary>找一個沒有世界物件的地表海洋格，建立海面地圖（水面層）。</summary>
        internal static Map CreateSeaMap(TestContext ctx)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            PlanetTile tile = PlanetTile.Invalid;
            for (int i = 0; i < surface.TilesCount; i++)
            {
                PlanetTile t = new PlanetTile(i, surface);
                if (t.Tile.WaterCovered && !Find.WorldObjects.AnyWorldObjectAt(t))
                {
                    tile = t;
                    break;
                }
            }
            ctx.Require(tile.Valid, "found a surface ocean tile");
            Map map = DebugActions_SeaSurface.CreateAt(tile);
            Current.Game.CurrentMap = map;
            return map;
        }

        internal static void LayHull(Map map, CellRect rect)
        {
            TerrainDef hull = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            foreach (IntVec3 c in rect) { map.terrainGrid.SetTerrain(c, hull); }
        }

        internal static Building_PillarFoot BuildPillar(Map sea, IntVec3 at)
        {
            Thing t = ThingMaker.MakeThing(DepthRimDefOf.DepthRim_PlatformPillar);
            t.SetFaction(Faction.OfPlayer);
            // 升降點是緊貼 9×9 四邊的任一格浮船
            return (Building_PillarFoot)GenSpawn.Spawn(t, at, sea, Rot4.North);
        }

        internal static void RemoveSeaMaps(Map sea)
        {
            Map platform = SeaLevels.PlatformLevelOf(sea);
            Current.Game.CurrentMap = DepthRimFixture.Home;
            if (platform != null)
            {
                PocketMapUtility.DestroyPocketMap(platform);
            }
            MapParent parent = sea.Parent;
            if (parent.HasMap)
            {
                Current.Game.DeinitAndRemoveMap(sea, false);
            }
            if (!parent.Destroyed)
            {
                parent.Destroy();
            }
        }

        /// <summary>
        /// H2：海面的兩層。新的海面地圖只有海；在船體平台旁蓋一根支柱 → 建立平台層（同尺寸、同格子、口袋地圖），
        /// 平台層同一位置開出 5×5 海上平台與支柱頂；第二根支柱共用同一張平台層。檢查放置規則、溫度與天空一致、
        /// 兩層的海面背景與遮罩（平台影子、浮船、支柱入水處）、水面層往上伸的柱身、平台層往下看的快照（只在水面層變了才重建）、
        /// 搭升降梯上去、拆支柱後範圍外的平台崩塌而上面的人掉到水面層、拆平台露出高空。兩層各截一張圖。
        /// </summary>
        public static IEnumerable<Wait> H2_SeaSurface(TestContext ctx)
        {
            Map sea = CreateSeaMap(ctx);
            TerrainDef openSea = DepthRimDefOf.DepthRim_OpenSea;
            TerrainDef openAir = DepthRimDefOf.DepthRim_OpenAir;
            TerrainDef deck = DepthRimDefOf.DepthRim_OceanPlatform;
            TerrainDef hullDef = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            ctx.Check(sea.AllCells.All(c => sea.terrainGrid.TerrainAt(c) == openSea), "a new sea surface map is only open sea");
            ctx.Check(!sea.AllCells.Any(c => c.Fogged(sea)), "no fog on the sea surface");
            ctx.Check(SeaLevels.IsSeaLevel(sea) && SeaLevels.PlatformLevelOf(sea) == null, "no platform level before any pillar");

            // 一艘浮船（船體平台）當碼頭，殖民者站在上面；支柱蓋在它南邊的海上
            IntVec3 center = sea.Center;
            CellRect dock = new CellRect(center.x - 3, center.z, 7, 3);
            LayHull(sea, dock);
            Pawn colonist = DepthRimFixture.SpawnColonist(sea, dock.CenterCell);
            yield return new WaitFrames(5);
            ctx.Require(sea.Parent.HasMap, "the sea surface map still exists");

            IntVec3 p1 = new IntVec3(center.x, 0, dock.minZ - 5);
            ThingDef pillarDef = DepthRimDefOf.DepthRim_PlatformPillar;
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(pillarDef, p1, Rot4.North, sea, godMode: true).Accepted, "a pillar can be built on open sea right next to a floating hull");
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(pillarDef, new IntVec3(dock.minX - 5, 0, dock.minZ + 1), Rot4.North, sea, godMode: true).Accepted, "on any side of the hull (west)");
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(pillarDef, new IntVec3(dock.maxX + 4, 0, dock.maxZ + 5), Rot4.North, sea, godMode: true).Accepted, "even touching the hull with just one cell (north-east)");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(pillarDef, p1 + new IntVec3(0, 0, -1), Rot4.North, sea, godMode: true).Accepted, "but not one cell away from the hull");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(pillarDef, new IntVec3(dock.maxX + 5, 0, dock.maxZ + 5), Rot4.North, sea, godMode: true).Accepted, "or only diagonally touching it");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(pillarDef, dock.CenterCell, Rot4.North, sea, godMode: true).Accepted, "and not on a floating hull");
            int mapsBefore = Find.Maps.Count;
            Building_PillarFoot foot1 = BuildPillar(sea, p1);
            yield return new WaitFrames(3);

            Map platform = SeaLevels.PlatformLevelOf(sea);
            ctx.Require(platform != null, "the first pillar created the platform level");
            ctx.Check(Find.Maps.Count == mapsBefore + 1, "exactly one new map");
            ctx.Check(platform.Parent is PocketMapParent pp && pp.sourceMap == sea, "the platform level is a pocket map of the sea level");
            ctx.Check(platform.Size == sea.Size && platform.Tile == sea.Tile, $"same size and world tile ({platform.Size} / {platform.Tile})");
            ctx.Check(SeaLevels.IsPlatformLevel(platform) && SeaLevels.SeaLevelOf(platform) == sea, "the two levels know each other");
            Building_PlatformPillar top1 = foot1.Top;
            ctx.Require(top1 != null && top1.Spawned && top1.Map == platform && top1.Position == p1, "the pillar top stands on the platform level right above the pillar");
            ctx.Check(top1.entrance == foot1 && foot1.GetOtherMap() == platform && top1.GetOtherMap() == sea, "the pillar is an elevator between the two levels");
            ctx.Check(foot1.LandingCell == p1 + new IntVec3(0, 0, 5) && dock.Contains(foot1.LandingCell), $"the elevator spot is on the dock ({foot1.LandingCell})");
            ctx.Check(top1.GetDestinationLocation() == foot1.LandingCell, "riding down lands on the elevator spot");
            ctx.Check(foot1.IsEnterable(out _) && top1.IsEnterable(out _), "the elevator can be used both ways");
            List<IntVec3> start = platform.AllCells.Where(c => platform.terrainGrid.TerrainAt(c) == deck).ToList();
            ctx.Check(start.Count == 25 && start.All(c => c.InHorDistOf(p1, 2.9f)), $"a 5x5 platform opened around the pillar top ({start.Count})");
            ctx.Check(platform.AllCells.Count(c => platform.terrainGrid.TerrainAt(c) == openAir) == platform.Area - 25, "the rest of the platform level is open air");
            MapComponent_PlatformSupport support = platform.GetComponent<MapComponent_PlatformSupport>();
            ctx.Check(support.Bases.Count == 1 && start.All(c => support.IsSupported(c)), "the top supports its starting platform");
            ctx.Check(Mathf.Abs(platform.mapTemperature.OutdoorTemp - sea.mapTemperature.OutdoorTemp) < 0.01f, $"the platform level has the sea level's temperature ({platform.mapTemperature.OutdoorTemp:F1} / {sea.mapTemperature.OutdoorTemp:F1})");
            ctx.Note($"sky glow: sea {sea.skyManager.CurSkyGlow:F2}, platform {platform.skyManager.CurSkyGlow:F2}");
            ctx.Check(Mathf.Abs(platform.skyManager.CurSkyGlow - sea.skyManager.CurSkyGlow) < 0.15f, "both levels share the same daylight");

            // 放置規則
            IntVec3 nextToStart = new IntVec3(p1.x + 3, 0, p1.z);
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(deck, nextToStart, Rot4.North, platform, godMode: true).Accepted, "ocean platform can be extended on the platform level within support");
            int beyond = Mathf.CeilToInt(top1.Ext.supportRadius) + 2;
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(deck, new IntVec3(p1.x, 0, p1.z + beyond), Rot4.North, platform, godMode: true).Accepted, $"not beyond any pillar's support ({beyond} cells away)");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(deck, new IntVec3(dock.maxX + 3, 0, dock.minZ), Rot4.North, sea, godMode: true).Accepted, "ocean platform cannot be laid at the water level");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(hullDef, nextToStart, Rot4.North, platform, godMode: true).Accepted, "a floating hull cannot be laid in the air");

            // 建築分頁：海上平台只在平台層顯示
            Map viewing = Find.CurrentMap;
            bool wasGod = DebugSettings.godMode;
            DebugSettings.godMode = false;
            Designator_Build deckDesignator = new Designator_Build(deck);
            Current.Game.CurrentMap = platform;
            // 2026-11-17：海上平台與平台支柱需要耐壓工程研究。沒研究時哪裡都不顯示；暫時完成研究再看平台層，之後還原進度
            ResearchProjectDef pressureEng = DefDatabase<ResearchProjectDef>.GetNamed("DepthRim_PressureEngineering");
            ctx.Check(deck.researchPrerequisites.Contains(pressureEng) && pillarDef.researchPrerequisites.Contains(pressureEng), "the ocean platform and pillar need pressure engineering");
            Dictionary<ResearchProjectDef, float> progress = HarmonyLib.Traverse.Create(Find.ResearchManager).Field("progress").GetValue<Dictionary<ResearchProjectDef, float>>();
            bool hadProgress = progress.TryGetValue(pressureEng, out float oldProgress);
            bool wasFinished = pressureEng.IsFinished;
            if (!wasFinished)
            {
                ctx.Check(!deckDesignator.Visible, "without the research the ocean platform is not in the build tab");
            }
            progress[pressureEng] = pressureEng.baseCost;
            bool onPlatform = deckDesignator.Visible;
            Current.Game.CurrentMap = sea;
            bool onSea = deckDesignator.Visible;
            Current.Game.CurrentMap = DepthRimFixture.Home;
            bool atHome = deckDesignator.Visible;
            if (hadProgress) { progress[pressureEng] = oldProgress; } else { progress.Remove(pressureEng); }
            Current.Game.CurrentMap = viewing;
            DebugSettings.godMode = wasGod;
            ctx.Check(onPlatform && !onSea && !atHome, $"the ocean platform shows in the build tab only on the platform level (platform {onPlatform}, sea {onSea}, home {atHome})");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(pillarDef, nextToStart + new IntVec3(2, 0, 0), Rot4.North, platform, godMode: true).Accepted, "pillars are built from the water, not on the platform");

            // 上下檢查：平台層同一位置有建築時不能蓋支柱
            IntVec3 p3 = new IntVec3(p1.x + 12, 0, p1.z);
            IntVec3 p3Spot = p3 + new IntVec3(0, 0, 5);
            sea.terrainGrid.SetTerrain(p3Spot, hullDef);
            platform.terrainGrid.SetTerrain(p3, deck);
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(pillarDef, p3, Rot4.North, sea, godMode: true).Accepted, "a pillar can go under empty platform");
            Thing above = DepthRimFixture.Spawn(ThingDef.Named("DepthRim_PressureHullWall"), p3 + new IntVec3(1, 0, 1), platform);
            AcceptanceReport blocked = GenConstruct.CanPlaceBlueprintAt(pillarDef, p3, Rot4.North, sea, godMode: true);
            ctx.Check(!blocked.Accepted, $"but not under a building on the platform level ({blocked.Reason})");
            above.Destroy(DestroyMode.Vanish);
            platform.terrainGrid.SetTerrain(p3, openAir);
            sea.terrainGrid.SetTerrain(p3Spot, openSea);

            // 第二根支柱：共用同一張平台層
            IntVec3 p2 = new IntVec3(p1.x + 26, 0, p1.z);
            CellRect dock2 = new CellRect(p2.x - 2, p2.z + 5, 5, 2);
            LayHull(sea, dock2);
            Building_PillarFoot foot2 = BuildPillar(sea, p2);
            yield return new WaitFrames(3);
            ctx.Check(Find.Maps.Count == mapsBefore + 1 && foot2.GetOtherMap() == platform, "the second pillar shares the platform level");
            ctx.Check(support.Bases.Count == 2 && foot2.Top?.Position == p2, "and has its own top");
            ctx.Check(platform.AllCells.Count(c => platform.terrainGrid.TerrainAt(c) == deck) == 50, "and its own 5x5 platform");

            // 海面背景：兩層都畫；遮罩相同（r = 平台層的平台，b = 浮船，g = 支柱入水處）
            MapComponent_SeaSurface seaComp = sea.GetComponent<MapComponent_SeaSurface>();
            MapComponent_SeaSurface platComp = platform.GetComponent<MapComponent_SeaSurface>();
            Material seaMat = seaComp.Material;
            Material platMat = platComp.Material;
            ctx.Require(seaMat != null && platMat != null, "both levels have a sea material");
            seaComp.RebuildMask();
            platComp.RebuildMask();
            int hullCells = dock.Area + dock2.Area;
            ctx.Check(seaComp.lastPlatformCells == 50 && seaComp.lastFloatingCells == hullCells && seaComp.lastPillarCells == 36, $"the water level's mask shows the platforms above, the hulls and both pillars ({seaComp.lastPlatformCells}, {seaComp.lastFloatingCells}, {seaComp.lastPillarCells})");
            ctx.Check(platComp.lastPlatformCells == 50 && platComp.lastFloatingCells == hullCells && platComp.lastPillarCells == 36, $"the platform level's mask matches ({platComp.lastPlatformCells}, {platComp.lastFloatingCells}, {platComp.lastPillarCells})");
            ctx.Check(seaMat.GetVector("_Height").x == 0f && Mathf.Abs(platMat.GetVector("_Height").x - MapComponent_SeaSurface.SeaDropCells) < 0.001f, "the water level sees the sea in place, the platform level sees it far below");
            SectionLayer_SeaSurface platSea = (SectionLayer_SeaSurface)platform.mapDrawer.SectionAt(new IntVec3(p1.x, 0, p1.z + 10)).GetLayer(typeof(SectionLayer_SeaSurface));
            platSea.Regenerate();
            ctx.Check(platSea.lastCells > 0, $"the platform level draws the sea under its open air ({platSea.lastCells})");

            // 水面層：柱身從入水處往上伸（2026-11-17 使用者決定不再檢查柱身的確切位置，只檢查有畫出來）
            Rect body = foot1.BodyRect;
            IntVec3 bodyTop = new IntVec3(p1.x, 0, Mathf.FloorToInt(body.yMax) - 1);
            SectionLayer_PlatformPillars seaPillars = (SectionLayer_PlatformPillars)sea.mapDrawer.SectionAt(bodyTop).GetLayer(typeof(SectionLayer_PlatformPillars));
            seaPillars.Regenerate();
            ctx.Check(seaPillars.lastPieces > 0, "the upper part of the pillar is drawn at the water level");
            IntVec3 topBodyBottom = new IntVec3(p1.x, 0, Mathf.FloorToInt(top1.PillarRect.yMin) + 1);
            SectionLayer_PlatformPillars platPillars = (SectionLayer_PlatformPillars)platform.mapDrawer.SectionAt(topBodyBottom).GetLayer(typeof(SectionLayer_PlatformPillars));
            platPillars.Regenerate();
            ctx.Check(platPillars.lastPieces > 0, "the platform level draws the pillar going down");

            // 平台層往下看：浮船與上面的建築（只在水面層變了才重建）
            float drop = MapComponent_SeaSurface.SeaDropCells;
            IntVec3 screen = SectionLayer_LowerLevel.ScreenCell(dock.CenterCell, drop);
            SectionLayer_LowerLevel lower = (SectionLayer_LowerLevel)platform.mapDrawer.SectionAt(screen).GetLayer(typeof(SectionLayer_LowerLevel));
            ctx.Require(lower != null, "the platform level has the lower-level layer");
            lower.Regenerate();
            ctx.Check(lower.lastCells > 0, $"the floating hull is drawn below the platform level ({lower.lastCells} cells)");
            Thing wall = DepthRimFixture.Spawn(ThingDef.Named("DepthRim_PressureHullWall"), new IntVec3(dock.minX, 0, dock.maxZ), sea);
            yield return new WaitFrames(2);
            ctx.Check(platComp.RefreshLowerLevelIfChanged(), "a new building at the water level triggers a refresh");
            ctx.Check(!platComp.RefreshLowerLevelIfChanged(), "no change, no refresh");
            lower.Regenerate();
            ctx.Check(lower.lastThings > 0, $"buildings at the water level are drawn below ({lower.lastThings})");

            // 平台底下的明暗（2026-11-15）：暗化層與柱身的材質兩層都有、共用水面層的一份遮罩；遊戲邏輯的光照在甲板下變暗、遠處不變
            ctx.Check(seaComp.ShadowMaterial != null && platComp.ShadowMaterial != null, "both levels have the platform-shadow overlay material");
            ctx.Check(seaComp.PillarMaterial(foot1.Ext) != null && platComp.PillarMaterial(top1.Ext) != null, "both levels have a height-shaded pillar material");
            ctx.Check(seaComp.Mask != null && seaComp.Mask == platComp.Mask, "the two levels share one shadow mask");
            IntVec3 underDeck = platform.AllCells.Where(c => platform.terrainGrid.TerrainAt(c) == deck).OrderBy(c => (c - p1).LengthHorizontalSquared).First();
            IntVec3 farSea = new IntVec3(dock.minX - 25, 0, dock.maxZ + 25);
            float underShade = seaComp.ShadowAt(underDeck);
            ctx.Check(underShade > 0.2f && seaComp.ShadowAt(farSea) < 0.01f, $"daylight is blocked under the deck ({underShade:F2}) and not out on the open sea");
            ctx.Check(platComp.ShadowAt(underDeck) == 0f, "nothing shades the platform level itself");
            float skyGlow = sea.skyManager.CurSkyGlow;
            float glowUnder = sea.glowGrid.GroundGlowAt(underDeck);
            ctx.Check(skyGlow <= 0.01f || glowUnder < sea.glowGrid.GroundGlowAt(farSea), $"the game's light level is lower under the deck ({glowUnder:F2} vs sky {skyGlow:F2})");

            // 截圖：平台層（往下看）與水面層（往上看）
            Current.Game.CurrentMap = platform;
            platComp.RefreshLowerLevelIfChanged();
            foreach (Wait w in ctx.ScreenshotOn(platform, new IntVec3(p1.x, 0, p1.z - 6), 16f, "platform_level_looking_down", 60)) { yield return w; }
            yield return new WaitFrames(30);
            Current.Game.CurrentMap = sea;
            foreach (Wait w in ctx.ScreenshotOn(sea, new IntVec3(p1.x, 0, p1.z + 6), 16f, "sea_level_looking_up", 60)) { yield return w; }
            yield return new WaitFrames(30);

            // 海面擺動與鏡頭：擺動幅度跟風速；鏡頭只在水面層、而且設定打開時才擺
            HarmonyLib.Traverse windField = HarmonyLib.Traverse.Create(sea.windManager).Field("cachedWindSpeed");
            float originalWind = windField.GetValue<float>();
            // 暫停遊戲：WindManager 每個 tick 都會重算風速，蓋掉測試設的值；鏡頭擺動用真實時間，暫停時照樣在擺
            TimeSpeed speedBefore = Find.TickManager.CurTimeSpeed;
            ctx.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            windField.SetValue(0.2f);
            seaComp.SnapSwayFactor();
            platComp.SnapSwayFactor();
            ctx.Check(Mathf.Abs(seaComp.SwayFactor - 1f) < 0.001f && Mathf.Abs(seaMat.GetVector("_Sway").x - SeaSway.BaseAmplitude) < 0.001f, $"calm wind: base sway ({seaMat.GetVector("_Sway")})");
            ctx.Check(Mathf.Abs(platComp.SwayFactor - seaComp.SwayFactor) < 0.001f, "the platform level uses the water level's wind");
            float MaxCameraOffset()
            {
                return Mathf.Abs(Patch_SeaCameraSway.lastOffset);
            }
            float calmBob = 0f;
            Current.Game.CurrentMap = sea;
            // 取樣滿一個鏡頭週期（真實秒數）：只取一小段的話，最大值看相位
            float calmBobEnd = Time.realtimeSinceStartup + SeaSway.CameraPeriod * 1.1f;
            while (Time.realtimeSinceStartup < calmBobEnd)
            {
                yield return new WaitFrames(1);
                calmBob = Mathf.Max(calmBob, MaxCameraOffset());
            }
            ctx.Check(calmBob > 0.003f && calmBob <= SeaSway.CameraAmplitude * 1.01f, $"the camera bobs very slightly at the water level ({calmBob:F4} cells)");
            DepthRimModSettings.Current.seaCameraSway = false;
            yield return new WaitFrames(3);
            ctx.Check(Patch_SeaCameraSway.lastOffset == 0f, "turning the setting off stops the bob");
            DepthRimModSettings.Current.seaCameraSway = true;
            Current.Game.CurrentMap = platform;
            yield return new WaitFrames(3);
            ctx.Check(Patch_SeaCameraSway.lastOffset == 0f, "no bob on the platform level (it stands on pillars)");
            Current.Game.CurrentMap = sea;
            foreach (Wait w in ctx.ScreenshotOn(sea, new IntVec3(p1.x - 12, 0, p1.z + 4), 12f, "sea_calm", 40)) { yield return w; }
            yield return new WaitFrames(30);

            windField.SetValue(1.9f);
            seaComp.SnapSwayFactor();
            ctx.Check(seaComp.SwayFactor >= 2f && seaMat.GetVector("_Sway").x >= 2f * SeaSway.BaseAmplitude - 0.001f, $"storm wind: the sea sways at least twice as much ({seaComp.SwayFactor:F2})");
            float stormBob = 0f;
            // 取樣滿一個鏡頭週期（真實秒數）：只取一小段的話，最大值看相位
            float stormBobEnd = Time.realtimeSinceStartup + SeaSway.CameraPeriod * 1.1f;
            while (Time.realtimeSinceStartup < stormBobEnd)
            {
                yield return new WaitFrames(1);
                stormBob = Mathf.Max(stormBob, MaxCameraOffset());
            }
            ctx.Check(stormBob > calmBob * 1.5f, $"and the camera bobs more ({calmBob:F4} -> {stormBob:F4} cells)");
            foreach (Wait w in ctx.ScreenshotOn(sea, new IntVec3(p1.x - 12, 0, p1.z + 4), 12f, "sea_storm", 40)) { yield return w; }
            yield return new WaitFrames(30);
            windField.SetValue(originalWind);
            seaComp.SnapSwayFactor();
            Find.TickManager.CurTimeSpeed = speedBefore;
            ctx.allowPause = false;

            // 搭升降梯上去
            colonist.drafter.Drafted = true;
            colonist.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterPortal, foot1), JobTag.Misc);
            IntVec3 upFrom = IntVec3.Invalid;
            WaitUntil up = new WaitUntil(() =>
            {
                if (colonist.Map == sea)
                {
                    upFrom = colonist.Position;
                }
                return colonist.Map == platform;
            }, 2500);
            yield return up;
            ctx.Check(!up.TimedOut && colonist.Map == platform && colonist.Position.InHorDistOf(p1, 3f), $"the colonist rode the pillar up to the platform ({colonist.Map}, {colonist.Position})");
            ctx.Check(upFrom == foot1.LandingCell, $"from the elevator spot ({upFrom})");

            // 再搭下去：落在升降點
            colonist.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterPortal, top1), JobTag.Misc);
            WaitUntil down = new WaitUntil(() => colonist.Map == sea, 2500);
            yield return down;
            ctx.Check(!down.TimedOut && colonist.Position == foot1.LandingCell, $"riding down lands on the elevator spot ({colonist.Map}, {colonist.Position})");

            // 拆第二根支柱：範圍外的平台崩塌，上面的人掉到水面層的浮船上
            Pawn faller = DepthRimFixture.SpawnColonist(platform, p2 + new IntVec3(0, 0, 2));
            foot2.Destroy(DestroyMode.Vanish);
            ctx.Check(!foot2.Top?.Spawned ?? true, "removing a pillar removes its top");
            ctx.Check(support.Bases.Count == 1 && support.PendingCount > 0 && support.PendingCount <= 25, $"its platform starts collapsing ({support.PendingCount})");
            for (int i = 0; i < 80 && support.PendingCount > 0; i++)
            {
                yield return new WaitTicks(15);
            }
            ctx.Check(platform.terrainGrid.TerrainAt(p2) == openAir, "the unsupported platform collapsed into open air");
            ctx.Check(faller.Spawned && faller.Map == sea && faller.Position.Standable(sea), $"the colonist on it fell to the water level ({faller.Map}, {faller.Position})");
            ctx.Check(platform.terrainGrid.TerrainAt(p1) == deck, "the first pillar's platform stays");

            // 平台結構：和支柱斷開的平台（DMSE 船體連通判定）即使還在支撐半徑內也會崩塌
            List<IntVec3> arm = new List<IntVec3>();
            for (int x = p1.x + 3; x <= p1.x + 8; x++) { arm.Add(new IntVec3(x, 0, p1.z)); }
            foreach (IntVec3 c in CellRect.FromLimits(p1.x + 9, p1.z - 1, p1.x + 11, p1.z + 1)) { arm.Add(c); }
            foreach (IntVec3 c in arm) { platform.terrainGrid.SetTerrain(c, deck); }
            yield return new WaitTicks(MapComponent_PlatformSupport.StructureCheckInterval * 2);
            ctx.Check(support.PendingCount == 0 && arm.All(c => support.IsSupported(c)), "a platform arm built out from the pillar's platform is supported");
            int piecesBefore = support.DeckPieces;
            IntVec3 cut = new IntVec3(p1.x + 5, 0, p1.z);
            PlatformRemovalUtility.Remove(cut, platform, doLeavings: false);
            yield return new WaitTicks(MapComponent_PlatformSupport.StructureCheckInterval * 2);
            List<IntVec3> cutOff = arm.Where(c => c.x > cut.x).ToList();
            ctx.Check(support.DeckPieces == piecesBefore + 1, $"cutting the arm splits the platform ({piecesBefore} -> {support.DeckPieces} pieces)");
            ctx.Check(cutOff.All(c => support.InRange(c) && support.IsPending(c)), $"the cut-off piece starts collapsing even though it is within support radius ({support.PendingCount}/{cutOff.Count})");
            ctx.Check(arm.Where(c => c.x < cut.x).All(c => !support.IsPending(c)) && !support.IsPending(p1), "the part still joined to the pillar stays");
            // 倒數中補上斷口：取消崩塌
            platform.terrainGrid.SetTerrain(cut, deck);
            yield return new WaitTicks(MapComponent_PlatformSupport.StructureCheckInterval * 2);
            ctx.Check(support.PendingCount == 0, $"rejoining the piece in time cancels the collapse ({support.PendingCount})");
            // 再切斷一次，等它塌掉
            PlatformRemovalUtility.Remove(cut, platform, doLeavings: false);
            for (int i = 0; i < 80 && (support.PendingCount > 0 || i < 2); i++)
            {
                yield return new WaitTicks(15);
            }
            ctx.Check(cutOff.All(c => platform.terrainGrid.TerrainAt(c) == openAir), "the cut-off piece collapsed into open air");
            ctx.Check(arm.Where(c => c.x < cut.x).All(c => platform.terrainGrid.TerrainAt(c) == deck), "the joined part is still there");

            // 拆平台：平台層露出高空，物品掉下去
            IntVec3 corner = new IntVec3(p1.x + 2, 0, p1.z + 2);
            PlatformRemovalUtility.Remove(corner, platform, doLeavings: false);
            ctx.Check(platform.terrainGrid.TerrainAt(corner) == openAir, $"a removed ocean platform leaves open air ({platform.terrainGrid.TerrainAt(corner)?.defName})");
            IntVec3 hullCorner = new IntVec3(dock.maxX, 0, dock.minZ);
            PlatformRemovalUtility.Remove(hullCorner, sea, doLeavings: false);
            ctx.Check(sea.terrainGrid.TerrainAt(hullCorner) == openSea, "a removed hull platform leaves open sea");

            // 地圖保留：平台層有人時水面層不移除
            colonist.Destroy(DestroyMode.Vanish);
            faller.Destroy(DestroyMode.Vanish);
            wall.Destroy(DestroyMode.Vanish);
            Current.Game.CurrentMap = DepthRimFixture.Home;
            Pawn keeper = DepthRimFixture.SpawnColonist(platform, p1 + new IntVec3(2, 0, 0));
            ctx.Check(!((WorldObject_SeaSurfaceSite)sea.Parent).ShouldRemoveMapNow(out _), "a colonist on the platform level keeps the sea surface");
            keeper.Destroy(DestroyMode.Vanish);
            RemoveSeaMaps(sea);
            yield return new WaitFrames(2);
            ctx.Check(!Find.Maps.Contains(platform) && !Find.Maps.Contains(sea), "both levels are removed together");
        }
    }
}
