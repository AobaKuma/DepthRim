using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>潛艇開局（場景 <c>DepthRim_SubmarineStart</c>）：開局格換到海洋層、開局地圖生成潛艇、殖民者與物品放進船艙。</summary>
    public static class Tests_SubmarineStart
    {
        private static readonly IntVec3 MapSize = new IntVec3(140, 1, 140);

        /// <summary>
        /// S7：(1) 場景載入、潛艇是遊戲內匯出的 <c>DR_Submarine_Test</c>；(2) 選地表的開局地點時，開局格換成附近的淺海海洋格、地圖生成器換成潛艇開局的；
        /// (3) 用潛艇開局的地圖生成器在那一格生成地圖：中央有深淵船塢與潛艇、船體看得見、蓋了屋頂、是乾的、屬於玩家、進家園區，開局點在船艙裡；
        /// (4) 三名殖民者與開局物品放在船艙格（不會掉到深淵）。最後拍一張、移除地圖。
        /// </summary>
        public static IEnumerable<Wait> S7_SubmarineStart(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            ScenarioDef scenario = DefDatabase<ScenarioDef>.GetNamedSilentFail("DepthRim_SubmarineStart");
            ctx.Require(scenario != null, "the submarine start scenario is loaded");
            ctx.Check(scenario.scenario.AllParts.All(p => !(p is ScenPart_PlayerPawnsArriveMethod)), "the scenario has no vanilla arrival method");
            ScenPart_SubmarineStart part = ScenPart_SubmarineStart.FromDef;
            ctx.Require(part != null, "the scenario has a submarine start part");
            ctx.Require(part.Structure != null && part.Structure.defName == "DR_Submarine_Test", $"the submarine is the exported structure ({part.Structure?.defName})");
            ctx.Check(ScenPart_SubmarineStart.Current == null, "the test game is not a submarine start");

            // ── 開局地點：地表格 → 附近的淺海海洋格 ──
            // 測試世界的開局地點是隨機的，可能在內陸（附近沒有淺海）：那就改選一個淺海海洋格正上方的地表格當「選定的地點」
            PlanetTile home = DepthRimFixture.Home.Tile;
            if (!part.FindOceanStart(home).Valid)
            {
                ctx.Check(true, "the inland home tile has no shallow sea nearby, so the site page would refuse it");
                home = OceanWorldView.TileAbove(part.RandomOceanStart());
                ctx.Note($"home is inland; using the surface tile {home} above a random shallow ocean tile as the chosen site");
            }
            PlanetTile ocean = part.FindOceanStart(home);
            ctx.Require(ocean.Valid, $"found a start tile in the ocean near the chosen site ({ocean})");
            ctx.Check(ocean.LayerDef == DepthRimDefOf.DepthRim_Ocean, "the start tile is on the ocean layer");
            ctx.Check(DepthTierUtility.MaxTierAt(ocean) <= part.maxTier, $"the start tile is no deeper than {part.maxTier} ({ocean.Tile.PrimaryBiome.defName})");
            ctx.Check(Find.WorldObjects.MapParentAt(ocean) == null, "the start tile has no map yet (the shaft's ocean map is skipped)");
            ctx.Check(Find.WorldGrid.TraversalDistanceBetween(OceanWorldView.TileBelow(home), ocean) <= part.searchRadius, "the start tile is within the search radius");

            GameInitData saved = Current.Game.InitData;
            Current.Game.InitData = new GameInitData { startingTile = home };
            part.RedirectStartingTile();
            ctx.Check(Find.GameInitData.startingTile == ocean, $"the starting tile is redirected to that ocean tile ({Find.GameInitData.startingTile})");
            ctx.Check(Find.GameInitData.mapGeneratorDef?.defName == "DepthRim_OceanStart", $"the map generator is the submarine start ({Find.GameInitData.mapGeneratorDef?.defName})");
            Current.Game.InitData = saved;

            // ── 開局地圖 ──
            MapParent site = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
            site.Tile = ocean;
            site.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(site);
            Map map = MapGenerator.GenerateMap(MapSize, site, part.MapGenerator);
            ctx.Require(map != null, "the start map generated");
            yield return new WaitFrames(2);

            CellRect rect = SubmarinePrefab.Footprint(part.Structure, map.Center, part.rot);
            List<IntVec3> hull = SubmarinePrefab.HullCells(map, rect);
            ctx.Note($"submarine footprint {rect}, {hull.Count} hull cells, map {map.Biome.defName}");
            ctx.Check(hull.Count > 600, $"the hull platform is laid ({hull.Count} cells)");
            CellRect berth = rect.ExpandedBy(part.shipyardMargin).ClipInsideMap(map);
            if (part.carveShipyard)
            {
                ctx.Check(berth.Cells.Where(c => !hull.Contains(c)).All(c => c.GetTerrain(map) == DepthRimDefOf.DepthRim_Abyss), "the rest of the shipyard is abyss");
            }
            else
            {
                // 直接坐在海床上：不挖深淵船塢，船體底下的海床被記住（離港後還原）
                MapComponent_SeabedMemory memory = map.GetComponent<MapComponent_SeabedMemory>();
                int onSeabed = hull.Count(c => memory.TryGetUnder(c, out _));
                int abyssAround = berth.Cells.Count(c => !hull.Contains(c) && c.GetTerrain(map) == DepthRimDefOf.DepthRim_Abyss);
                ctx.Note($"{onSeabed} of {hull.Count} hull cells rest on seabed; {abyssAround} abyss cells around the hull");
                ctx.Check(onSeabed > hull.Count * 0.9f, $"the submarine sits directly on the seabed ({onSeabed}/{hull.Count} hull cells over remembered seabed)");
                ctx.Check(abyssAround < berth.Area / 10, $"no abyss dock is carved around the hull ({abyssAround} abyss cells)");
                ctx.Check(berth.Cells.Where(c => !hull.Contains(c)).All(c => c.GetEdifice(map) == null || c.GetEdifice(map).Faction == Faction.OfPlayer),
                    "the seabed around the hull is cleared of rocks");
                IntVec3 sideCell = hull.Where(c => !hull.Contains(c + IntVec3.South) && (c + IntVec3.South).InBounds(map)).FirstOrDefault(c => SectionLayer_HullPlatformProps.RestsOnSeabed(c, c + IntVec3.South, map));
                ctx.Check(sideCell.IsValid, "the hull's south edge rests on seabed, so its side shows only the freeboard");
            }
            ctx.Check(hull.All(c => c.Roofed(map)), "every hull cell is roofed");
            ctx.Check(rect.ExpandedBy(part.shipyardMargin).ClipInsideMap(map).Cells.All(c => !map.fogGrid.IsFogged(c)), "the shipyard and the hull are not fogged");
            ctx.Check(hull.All(c => map.areaManager.Home[c]), "the hull is in the home area");

            List<Building> buildings = rect.Cells.SelectMany(c => c.GetThingList(map)).OfType<Building>().Distinct().ToList();
            ctx.Note("buildings: " + string.Join(", ", buildings.GroupBy(b => b.def.defName).Select(g => $"{g.Key}×{g.Count()}")));
            ctx.Check(buildings.All(b => b.Faction == Faction.OfPlayer), "everything belongs to the player");
            foreach (string def in new[] { "DepthRim_Keel", "DepthRim_Helm", "DepthRim_Thruster", "DepthRim_MoonPool" })
            {
                ctx.Check(buildings.Any(b => b.def.defName == def), $"the submarine has a {def}");
            }
            ctx.Check(buildings.Select(b => b.TryGetComp<CompRefuelable>()).Where(f => f != null).All(f => f.IsFull), "fuelled buildings start full");

            IntVec3 start = ScenPart_SubmarineStart.StartSpot(map, rect, hull);
            ctx.Check(hull.Contains(start) && start.Standable(map), $"the start spot is a standable hull cell ({start})");

            // ── 艙室：乾、密閉 ──
            yield return new WaitTicks(MapComponent_Flooding.UpdateInterval + 5);
            MapComponent_Flooding flooding = map.Flooding();
            List<Room> rooms = hull.Where(c => c.GetEdifice(map) == null).Select(c => c.GetRoom(map))
                .Where(r => r != null && !r.IsDoorway).Distinct().ToList();
            ctx.Note($"{rooms.Count} rooms: " + string.Join(" ", rooms.Select(r => $"{r.CellCount}c/{(flooding.IsOpenWater(r) ? "open" : flooding.LevelOf(r).ToString("0.00"))}")));
            ctx.Check(rooms.Count >= 3, $"the hull has several rooms ({rooms.Count})");
            ctx.Check(rooms.All(r => !flooding.IsOpenWater(r)), "every room is sealed (no open water inside the hull)");
            ctx.Check(rooms.All(r => flooding.LevelOf(r) < 0.01f), "every room starts dry");

            // ── 殖民者與開局物品 ──
            List<Pawn> pawns = Enumerable.Range(0, 3).Select(_ => PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f))).ToList();
            List<Thing> items = new List<Thing>
            {
                ThingMaker.MakeThing(ThingDef.Named("DepthRim_DivingSuit")),
                MakeStack(ThingDefOf.MealSurvivalPack, 40),
                MakeStack(ThingDefOf.Steel, 300),
            };
            ScenPart_SubmarineStart.PlaceColonists(map, hull, start, pawns, items);
            ctx.Check(pawns.All(p => p.Spawned && p.Map == map && hull.Contains(p.Position)), "the three colonists are aboard");
            ctx.Check(pawns.All(p => p.Position.Roofed(map) && p.Position.Standable(map)), "they stand under the roof");
            ctx.Check(items.All(t => t.Spawned && hull.Contains(t.Position)), $"the starting items are inside the hull ({string.Join(", ", items.Select(t => t.LabelShort + "@" + t.PositionHeld))})");

            ctx.Note($"before screenshot: fogged hull cells {hull.Count(c => map.fogGrid.IsFogged(c))}, fogged map cells {map.AllCells.Count(c => map.fogGrid.IsFogged(c))}/{map.Area}, " +
                     $"roofed hull cells {hull.Count(c => c.Roofed(map))}, open-water hull cells {hull.Count(c => c.GetRoom(map) is Room r && flooding.IsOpenWater(r))}");
            foreach (Wait w in ctx.ScreenshotOn(map, start, 24f, "submarine_start")) { yield return w; }

            Current.Game.CurrentMap = DepthRimFixture.Home;
            Current.Game.DeinitAndRemoveMap(map, false);
            if (!site.Destroyed) { site.Destroy(); }
            yield return new WaitFrames(5);
        }

        /// <summary>
        /// G1：真的用潛艇開局開一局新遊戲（run.ps1 -Scenario DepthRim_SubmarineStart -Only G1）。
        /// 原版的新遊戲流程完整跑過（只少了選地點的頁面）：開局地圖在海洋層、玩家據點在那一格、潛艇在船塢裡、殖民者與開局物品在船艙、
        /// 起始研究完成、遊戲沒有報錯；然後跑兩天，殖民者都活著、沒有溺水、艙室保持乾燥、屋頂沒有塌；最後存檔再讀檔，一切還在。
        /// </summary>
        public static IEnumerable<Wait> G1_NewGame(TestContext ctx)
        {
            ctx.Require(ScenarioOverride.Active && Find.Scenario.name == ScenarioOverride.Scenario.scenario.name, $"the game started with the submarine start scenario ({Find.Scenario.name})");
            ScenPart_SubmarineStart part = ScenPart_SubmarineStart.Current;
            ctx.Require(part != null, "the scenario has the submarine start part");
            int errorsAtStart = ErrorCount();
            ctx.Check(errorsAtStart == 0, $"no errors while starting the game ({errorsAtStart}{FirstErrors()})");

            Map map = Find.CurrentMap;
            ctx.Note($"start map {map.uniqueID} tile {map.Tile} biome {map.Biome.defName} parent {map.Parent?.GetType().Name} '{map.Parent?.LabelCap}'");
            ctx.Check(map.Tile.LayerDef == DepthRimDefOf.DepthRim_Ocean, "the start map is on the ocean layer");
            ctx.Check(DepthTierUtility.MaxTierAt(map.Tile) <= part.maxTier, $"the start tile is no deeper than {part.maxTier}");
            ctx.Check(map.Parent is Settlement s && s.Faction == Faction.OfPlayer, "the map belongs to the player's settlement");
            ctx.Check(Find.GameInfo.startingTile == map.Tile, "the game's starting tile is that ocean tile");
            ctx.Check(map.IsPlayerHome, "the start map is the player's home");
            ctx.Check(Find.Maps.Count == 1, $"only the start map exists ({Find.Maps.Count})");

            CellRect rect = SubmarinePrefab.Footprint(part.Structure, map.Center, part.rot);
            List<IntVec3> hull = SubmarinePrefab.HullCells(map, rect);
            ctx.Check(hull.Count > 600, $"the submarine is docked at the map centre ({hull.Count} hull cells)");
            ctx.Check(hull.All(c => c.Roofed(map) && !map.fogGrid.IsFogged(c)), "the hull is roofed and visible");
            ctx.Check(hull.All(c => map.areaManager.Home[c]), "the hull is in the home area");

            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            ctx.Check(colonists.Count == 3, $"three colonists ({colonists.Count})");
            ctx.Check(colonists.All(p => hull.Contains(p.Position)), $"they are aboard ({string.Join(" ", colonists.Select(p => p.Position))})");
            ctx.Check(Find.CameraDriver.MapPosition.InHorDistOf(rect.CenterCell, 60f), $"the camera starts on the submarine ({Find.CameraDriver.MapPosition})");

            int Count(string def) => hull.SelectMany(c => c.GetThingList(map)).Where(t => t.def.defName == def).Sum(t => t.stackCount);
            ctx.Check(Count("DepthRim_DivingSuit") >= 3, $"three diving suits aboard ({Count("DepthRim_DivingSuit")})");
            ctx.Check(Count("MedicineIndustrial") >= 20 && Count("Chemfuel") >= 150 && Count("Gun_BoltActionRifle") >= 1, "medicine, chemfuel and guns aboard");
            ctx.Check(Count("MealSurvivalPack") >= 40, $"survival meals aboard ({Count("MealSurvivalPack")})");
            List<Thing> strays = map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.Faction == Faction.OfPlayer && !hull.Contains(t.PositionHeld)).ToList();
            ctx.Check(strays.Count == 0, $"no starting item fell outside the hull ({string.Join(", ", strays.Select(t => t.LabelShort))})");
            foreach (string r in new[] { "DepthRim_UnderwaterSurvival", "DepthRim_PressureEngineering", "DepthRim_SubmarineConstruction" })
            {
                ctx.Check(DefDatabase<ResearchProjectDef>.GetNamed(r).IsFinished, $"{r} is researched");
            }
            foreach (Wait w in ctx.ScreenshotOn(map, ScenPart_SubmarineStart.StartSpot(map, rect, hull), 30f, "newgame_start")) { yield return w; }

            // ── 跑兩天 ──
            MapComponent_Flooding flooding = map.Flooding();
            int roofsBefore = hull.Count(c => c.Roofed(map));
            yield return new WaitTicks(GenDate.TicksPerDay * 2);
            colonists = map.mapPawns.FreeColonists.ToList();
            ctx.Check(colonists.Count == 3 && colonists.All(p => !p.Dead && !p.Downed), $"after two days all three colonists are fine ({string.Join(", ", colonists.Select(p => p.LabelShort + (p.Downed ? " downed" : "")))})");
            ctx.Check(colonists.All(p => p.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Drowning) == null), "nobody is drowning");
            ctx.Check(hull.Count(c => c.Roofed(map)) == roofsBefore, $"no hull roof collapsed ({hull.Count(c => c.Roofed(map))}/{roofsBefore})");
            List<Room> rooms = hull.Where(c => c.GetEdifice(map) == null).Select(c => c.GetRoom(map)).Where(r => r != null && !r.IsDoorway).Distinct().ToList();
            ctx.Check(rooms.All(r => !flooding.IsOpenWater(r) && flooding.LevelOf(r) < 0.05f), $"every compartment is still dry ({string.Join(" ", rooms.Where(r => flooding.IsOpenWater(r) || flooding.LevelOf(r) >= 0.05f).Select(r => r.CellCount + "c"))})");
            int errorsAfterDays = ErrorCount();
            ctx.Check(errorsAfterDays == 0, $"no errors in two days of play ({errorsAfterDays}{FirstErrors()})");
            foreach (Wait w in ctx.ScreenshotOn(map, ScenPart_SubmarineStart.StartSpot(map, rect, hull), 30f, "newgame_day2")) { yield return w; }

            // ── 存檔再讀檔 ──
            Game before = Current.Game;
            int mapTile = map.Tile.tileId;
            GameDataSaveLoader.SaveGame(DepthRimFixture.TestSaveName);
            LongEventHandler.QueueLongEvent(() => SavedGameLoaderNow.LoadGameFromSaveFileNow(DepthRimFixture.TestSaveName), "LoadingLongEvent", false, null);
            yield return new WaitRealtime(() => Current.Game != before && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null
                                                      && !LongEventHandler.AnyEventNowOrWaiting, 120f);
            ctx.Require(Current.Game != before, "the game reloaded");
            yield return new WaitFrames(30);
            map = Find.CurrentMap;
            ctx.Check(map.Tile.LayerDef == DepthRimDefOf.DepthRim_Ocean && map.Tile.tileId == mapTile, "after loading, the start map is still on the ocean tile");
            ctx.Check(map.Parent is Settlement && map.IsPlayerHome, "it is still the player's home settlement");
            ctx.Check(ScenPart_SubmarineStart.Current != null, "the scenario part survived the save");
            hull = SubmarinePrefab.HullCells(map, rect);
            ctx.Check(hull.Count > 600 && map.mapPawns.FreeColonistsSpawned.Count == 3, $"the submarine and the crew are still there ({hull.Count} hull cells, {map.mapPawns.FreeColonistsSpawned.Count} colonists)");
            yield return new WaitTicks(600);
            int errorsAfterLoad = ErrorCount();
            ctx.Check(errorsAfterLoad == 0, $"no errors after loading ({errorsAfterLoad}{FirstErrors()})");
        }

        private static int ErrorCount()
        {
            lock (ScenarioOverride.Errors) { return ScenarioOverride.Errors.Count; }
        }

        private static string FirstErrors()
        {
            lock (ScenarioOverride.Errors)
            {
                return ScenarioOverride.Errors.Count == 0 ? "" : ": " + string.Join(" | ", ScenarioOverride.Errors.Take(3));
            }
        }

        private static Thing MakeStack(ThingDef def, int count)
        {
            Thing t = ThingMaker.MakeThing(def);
            t.stackCount = count;
            return t;
        }
    }
}
