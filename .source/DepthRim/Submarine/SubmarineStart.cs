using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
using Fortified.Structures;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇開局（場景 <c>DepthRim_SubmarineStart</c>）：殖民者一開始就在一艘停在船塢裡的潛艇上，船塢在選定地點附近的淺海海床。
    ///
    /// <para>流程：</para>
    /// <list type="number">
    /// <item>選地點時照常選地表格（海面也可以）；<see cref="FindOceanStart"/> 從正下方的海洋格往外找最近、不超過 <see cref="maxTier"/>
    /// 的海洋格。附近找不到海就不能按「下一步」（<see cref="Patch_SubmarineStart_SelectSite"/>）。</item>
    /// <item>建立玩家據點之前（<see cref="Patch_SubmarineStart_PlayerFaction"/>）把開局格換成那個海洋格，地圖生成器換成
    /// <see cref="mapGenerator"/>（海洋地圖加上 <see cref="GenStep_SubmarineStart"/>）。</item>
    /// <item>地圖生成的最後一步（在海洋迷霧之後）在地圖中央挖深淵船塢、用 FFF 生成 <see cref="structure"/>，
    /// 把殖民者與開局物品放進船艙（<see cref="GenerateStart"/>）。</item>
    /// </list>
    /// 這個場景沒有原版的「抵達方式」，殖民者與開局物品（<see cref="ScenPart.PlayerStartingThings"/>）都由這裡放。
    /// </summary>
    public class ScenPart_SubmarineStart : ScenPart
    {
        /// <summary>預設生成的潛艇：遊戲內用 FFF 匯出的那一艘（<c>1.7/Defs/Structures.xml</c>）。</summary>
        public const string DefaultStructure = "DR_Submarine_Test";

        public FFF_StructureDef structure;

        public MapGeneratorDef mapGenerator;

        /// <summary>開局海域最深到哪一層（預設淺海：船殼還沒升級、也還沒有加壓艙）。</summary>
        public DepthTier maxTier = DepthTier.Shallow;

        /// <summary>從選定地點正下方往外找海洋格的範圍（世界格數）。</summary>
        public int searchRadius = 6;

        /// <summary>
        /// true = 在海床上挖一個深淵船塢，潛艇停在船塢裡（離港後留下深淵坑）；
        /// false（預設）= 潛艇直接坐在海床上（離港後海床由 <see cref="MapComponent_SeabedMemory"/> 還原）。
        /// </summary>
        public bool carveShipyard;

        /// <summary>船體佔地每邊再多幾格：挖船塢時是深淵的寬度，坐在海床上時是清空並揭開迷霧的範圍。</summary>
        public int shipyardMargin = 2;

        public Rot4 rot = Rot4.North;

        /// <summary>目前場景裡的潛艇開局（沒有時為 null）。</summary>
        public static ScenPart_SubmarineStart Current => Find.Scenario?.AllParts.OfType<ScenPart_SubmarineStart>().FirstOrDefault();

        /// <summary>場景定義裡的那一份（測試在一般遊戲裡生成開局地圖時用）。</summary>
        public static ScenPart_SubmarineStart FromDef => DefDatabase<ScenarioDef>.GetNamedSilentFail("DepthRim_SubmarineStart")?.scenario?.AllParts
            .OfType<ScenPart_SubmarineStart>().FirstOrDefault();

        public FFF_StructureDef Structure => structure ?? DefDatabase<FFF_StructureDef>.GetNamedSilentFail(DefaultStructure) ?? SubmarinePrefab.Default;

        public MapGeneratorDef MapGenerator => mapGenerator ?? DefDatabase<MapGeneratorDef>.GetNamed("DepthRim_OceanStart");

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref structure, "structure");
            Scribe_Defs.Look(ref mapGenerator, "mapGenerator");
            Scribe_Values.Look(ref maxTier, "maxTier", DepthTier.Shallow);
            Scribe_Values.Look(ref searchRadius, "searchRadius", 6);
            Scribe_Values.Look(ref carveShipyard, "carveShipyard", false);
            Scribe_Values.Look(ref shipyardMargin, "shipyardMargin", 2);
            Scribe_Values.Look(ref rot, "rot", Rot4.North);
        }

        public override string Summary(Scenario scen)
        {
            return "DepthRim.SubmarineStart.Summary".Translate(Structure?.defName ?? "?", maxTier.Label());
        }

        public override void DoEditInterface(Listing_ScenEdit listing)
        {
            Rect rect = listing.GetScenPartRect(this, ScenPart.RowHeight);
            if (Widgets.ButtonText(rect, Structure?.defName ?? "-"))
            {
                RimWorld.FloatMenuUtility.MakeMenu(DefDatabase<FFF_StructureDef>.AllDefs, d => d.defName, d => delegate { structure = d; });
            }
        }

        public override int GetHashCode()
        {
            return base.GetHashCode() ^ (structure?.GetHashCode() ?? 0) ^ ((int)maxTier << 8);
        }

        // ==================================================================
        //  開局地點
        // ==================================================================

        /// <summary>潛艇能在這個海洋格開局：有海（不是陸塊）、不比 <see cref="maxTier"/> 深、上面還沒有地圖。</summary>
        public bool IsGoodStartTile(PlanetTile tile)
        {
            if (!tile.Valid || tile.LayerDef != DepthRimDefOf.DepthRim_Ocean)
            {
                return false;
            }
            BiomeDef biome = tile.Tile?.PrimaryBiome;
            if (biome == null || OceanWorldView.IsLandmass(biome) || biome.GetModExtension<WaterMapExtension>() == null)
            {
                return false;
            }
            return DepthTierUtility.MaxTierAt(tile) <= maxTier && Find.WorldObjects.MapParentAt(tile) == null;
        }

        /// <summary>選定的地表格附近的開局海洋格：從正下方往外一圈一圈找，最多 <see cref="searchRadius"/> 圈；找不到回傳 Invalid。</summary>
        public PlanetTile FindOceanStart(PlanetTile chosen)
        {
            PlanetLayer ocean = Find.WorldGrid?.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean);
            PlanetTile root = OceanWorldView.TileBelow(chosen, ocean);
            if (!root.Valid)
            {
                return PlanetTile.Invalid;
            }
            HashSet<int> seen = new HashSet<int> { root.tileId };
            List<PlanetTile> ring = new List<PlanetTile> { root };
            List<PlanetTile> neighbors = new List<PlanetTile>();
            for (int step = 0; step <= searchRadius && ring.Count > 0; step++)
            {
                // 同一圈裡取離選定地點最近的
                PlanetTile best = PlanetTile.Invalid;
                float bestDist = float.MaxValue;
                Vector3 from = chosen.Layer.GetTileCenter(chosen).normalized;
                foreach (PlanetTile t in ring)
                {
                    if (!IsGoodStartTile(t))
                    {
                        continue;
                    }
                    float d = Vector3.Angle(from, ocean.GetTileCenter(t).normalized);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = t;
                    }
                }
                if (best.Valid)
                {
                    return best;
                }
                List<PlanetTile> next = new List<PlanetTile>();
                foreach (PlanetTile t in ring)
                {
                    neighbors.Clear();
                    ocean.GetTileNeighbors(t.tileId, neighbors);
                    foreach (PlanetTile n in neighbors)
                    {
                        if (seen.Add(n.tileId))
                        {
                            next.Add(n);
                        }
                    }
                }
                ring = next;
            }
            return PlanetTile.Invalid;
        }

        /// <summary>隨機一個開局海洋格（「隨機地點」按鈕與找不到海時的保底）。</summary>
        public PlanetTile RandomOceanStart()
        {
            PlanetLayer ocean = Find.WorldGrid?.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean);
            if (ocean == null)
            {
                return PlanetTile.Invalid;
            }
            for (int i = 0; i < 4000; i++)
            {
                PlanetTile t = new PlanetTile(Rand.Range(0, ocean.TilesCount), ocean);
                if (IsGoodStartTile(t))
                {
                    return t;
                }
            }
            return PlanetTile.Invalid;
        }

        /// <summary>建立玩家據點之前：開局格換成海洋格、地圖生成器換成潛艇開局的。</summary>
        public void RedirectStartingTile()
        {
            GameInitData init = Find.GameInitData;
            if (init == null)
            {
                return;
            }
            PlanetTile chosen = init.startingTile;
            PlanetTile ocean = IsGoodStartTile(chosen) ? chosen : FindOceanStart(chosen);
            if (!ocean.Valid)
            {
                ocean = RandomOceanStart();
                Log.Warning($"[DepthRim] Submarine start: no {maxTier} ocean near tile {chosen}; using random ocean tile {ocean}.");
            }
            if (!ocean.Valid)
            {
                Log.Error("[DepthRim] Submarine start: no ocean tile to start on; starting on the surface.");
                return;
            }
            init.startingTile = ocean;
            init.mapGeneratorDef = MapGenerator;
        }

        // ==================================================================
        //  開局地圖
        // ==================================================================

        /// <summary>
        /// 在地圖中央挖船塢、生成潛艇；<paramref name="placeColonists"/> 時把開局殖民者與物品放進船艙。回傳潛艇佔地。
        /// 地圖生成期間（海洋迷霧之後）由 <see cref="GenStep_SubmarineStart"/> 呼叫。
        /// </summary>
        public CellRect GenerateStart(Map map, bool placeColonists)
        {
            FFF_StructureDef def = Structure;
            if (def == null)
            {
                Log.Error("[DepthRim] Submarine start: no submarine structure def.");
                return CellRect.Empty;
            }
            IntVec3 center = map.Center;
            CellRect rect = SubmarinePrefab.Footprint(def, center, rot);
            CellRect yard = rect.ExpandedBy(shipyardMargin).ClipInsideMap(map);
            map.regionAndRoomUpdater.Enabled = true;
            if (carveShipyard)
            {
                SubmarinePrefab.CarveShipyard(map, yard);
            }
            else
            {
                SubmarinePrefab.ClearArea(map, yard);
            }
            SubmarinePrefab.Spawn(map, center, rot, Faction.OfPlayer, def, duringMapGen: true);

            // 船塢（或船邊的海床）看得見（FFF 只揭開船體佔地）；船體進家園區
            foreach (IntVec3 c in yard)
            {
                map.fogGrid.Unfog(c);
            }
            List<IntVec3> hull = SubmarinePrefab.HullCells(map, rect);
            foreach (IntVec3 c in hull)
            {
                map.areaManager.Home[c] = true;
            }
            // 下潛要有壓載（M6）：開局選的結構沒有或不夠時補上，免得開局的潛艇潛不下去或換深度變慢
            Ballast.FillHull(map, new HashSet<IntVec3>(hull), Faction.OfPlayer);
            foreach (Thing t in rect.Cells.SelectMany(c => c.GetThingList(map)).Distinct().ToList())
            {
                CompRefuelable fuel = t.TryGetComp<CompRefuelable>();
                if (fuel != null && t.Faction == Faction.OfPlayer)
                {
                    fuel.Refuel(fuel.Props.fuelCapacity);
                }
            }

            IntVec3 start = StartSpot(map, rect, hull);
            Verse.MapGenerator.PlayerStartSpot = start;
            if (placeColonists && Find.GameInitData != null)
            {
                PlaceColonists(map, hull, start, Find.GameInitData.startingAndOptionalPawns, CollectStartingItems());
            }
            return rect;
        }

        /// <summary>鏡頭與殖民者的起點：艦橋（DMSE 控制台）旁邊，沒有艦橋時用船體中央。</summary>
        public static IntVec3 StartSpot(Map map, CellRect rect, List<IntVec3> hull)
        {
            Thing helm = rect.Cells.SelectMany(c => c.GetThingList(map)).FirstOrDefault(t => t.TryGetComp<CompTransferConsole>() != null);
            IntVec3 near = helm?.Position ?? rect.CenterCell;
            IntVec3 best = IntVec3.Invalid;
            float bestDist = float.MaxValue;
            foreach (IntVec3 c in hull)
            {
                if (!c.Standable(map))
                {
                    continue;
                }
                float d = c.DistanceToSquared(near);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best.IsValid ? best : near;
        }

        /// <summary>把殖民者放在起點附近的船艙格、開局物品放在船艙裡（不會掉到船塢的深淵上）。</summary>
        public static void PlaceColonists(Map map, List<IntVec3> hull, IntVec3 start, List<Pawn> pawns, List<Thing> items)
        {
            HashSet<IntVec3> inside = new HashSet<IntVec3>(hull);
            List<IntVec3> spots = hull.Where(c => c.Standable(map) && c.Roofed(map) && c.GetDoor(map) == null && c.GetFirstItem(map) == null)
                .OrderBy(c => c.DistanceToSquared(start)).ToList();
            int pool = Mathf.Min(spots.Count, Mathf.Max(8, (pawns?.Count ?? 0) * 4));
            List<IntVec3> near = spots.Take(pool).InRandomOrder().ToList();
            int i = 0;
            foreach (Pawn pawn in pawns ?? new List<Pawn>())
            {
                if (pawn == null || pawn.Spawned)
                {
                    continue;
                }
                IntVec3 cell = near.Count > 0 ? near[i++ % near.Count] : start;
                GenSpawn.Spawn(pawn, cell, map);
            }
            foreach (Thing item in items ?? new List<Thing>())
            {
                if (item == null || item.Spawned || item.Destroyed)
                {
                    continue;
                }
                if (item.def.CanHaveFaction)
                {
                    item.SetFactionDirect(Faction.OfPlayer);
                }
                if (!GenPlace.TryPlaceThing(item, start, map, ThingPlaceMode.Near, extraValidator: c => inside.Contains(c)))
                {
                    GenPlace.TryPlaceThing(item, start, map, ThingPlaceMode.Near);
                }
            }
        }

        /// <summary>場景給的開局物品與每個殖民者的隨身物品（原版由「抵達方式」收集，這個場景沒有抵達方式）。</summary>
        public static List<Thing> CollectStartingItems()
        {
            List<Thing> list = new List<Thing>();
            foreach (ScenPart part in Find.Scenario.AllParts)
            {
                try
                {
                    IEnumerable<Thing> things = part?.PlayerStartingThings();
                    if (things != null)
                    {
                        list.AddRange(things.Where(t => t != null));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[DepthRim] {part.GetType().Name}.PlayerStartingThings threw: {ex}");
                }
            }
            GameInitData init = Find.GameInitData;
            if (init?.startingPossessions != null)
            {
                foreach (Pawn pawn in init.startingAndOptionalPawns)
                {
                    if (pawn != null && init.startingPossessions.TryGetValue(pawn, out List<ThingDefCount> possessions) && possessions != null)
                    {
                        list.AddRange(possessions.Select(StartingPawnUtility.GenerateStartingPossession).Where(t => t != null));
                    }
                }
            }
            return list;
        }
    }

    /// <summary>
    /// 潛艇開局地圖的最後一步：挖船塢、生成潛艇、放殖民者。排在海洋迷霧（1500）之後，
    /// 否則迷霧步驟會把整艘潛艇重新蓋上迷霧（船艙有屋頂，迷霧只從露天的海床揭開）。
    /// 不用原版的 ScenParts 步驟（875）：那時海洋地圖的深淵與熱液泉還沒生成。
    /// </summary>
    public class GenStep_SubmarineStart : GenStep
    {
        public override int SeedPart => 0x5B9E57A;

        public override void Generate(Map map, GenStepParams parms)
        {
            ScenPart_SubmarineStart part = ScenPart_SubmarineStart.Current ?? ScenPart_SubmarineStart.FromDef;
            if (part == null)
            {
                Log.Error("[DepthRim] GenStep_SubmarineStart: no submarine start scenario part.");
                return;
            }
            bool newGame = Find.GameInitData != null && Verse.Current.ProgramState == ProgramState.MapInitializing;
            part.GenerateStart(map, placeColonists: newGame);
        }
    }

    /// <summary>建立玩家據點之前把開局格換成海洋格（原版 <see cref="ScenPart_PlayerFaction.PreMapGenerate"/> 在開局格上建據點）。</summary>
    [HarmonyPatch(typeof(ScenPart_PlayerFaction), nameof(ScenPart_PlayerFaction.PreMapGenerate))]
    public static class Patch_SubmarineStart_PlayerFaction
    {
        public static void Prefix()
        {
            ScenPart_SubmarineStart.Current?.RedirectStartingTile();
        }
    }

    /// <summary>
    /// 選地點：潛艇開局可以選海面（原版不能在水上建據點），但附近要有夠淺的海；
    /// 開局據點其實建在海洋層，地表格上有沒有別的據點不影響。
    /// </summary>
    [HarmonyPatch(typeof(Page_SelectStartingSite), "CanDoNext")]
    public static class Patch_SubmarineStart_SelectSite
    {
        public static bool Prefix(ref bool __result)
        {
            ScenPart_SubmarineStart part = ScenPart_SubmarineStart.Current;
            PlanetTile selected = Find.WorldInterface.SelectedTile;
            if (part == null || !selected.Valid)
            {
                return true;
            }
            if (part.FindOceanStart(selected).Valid)
            {
                __result = true;
                return false;
            }
            Messages.Message("DepthRim.SubmarineStart.NoSea".Translate(part.maxTier.Label(), part.searchRadius), MessageTypeDefOf.RejectInput, historical: false);
            __result = false;
            return false;
        }
    }

    /// <summary>潛艇開局的「隨機地點」：隨機一個夠淺的海洋格，選它正上方的地表格。</summary>
    [HarmonyPatch(typeof(TileFinder), nameof(TileFinder.RandomStartingTile))]
    public static class Patch_SubmarineStart_RandomTile
    {
        public static bool Prefix(ref PlanetTile __result)
        {
            if (Verse.Current.ProgramState != ProgramState.Entry)
            {
                return true;
            }
            ScenPart_SubmarineStart part = ScenPart_SubmarineStart.Current;
            PlanetTile ocean = part?.RandomOceanStart() ?? PlanetTile.Invalid;
            if (!ocean.Valid)
            {
                return true;
            }
            __result = OceanWorldView.TileAbove(ocean);
            return !__result.Valid;
        }
    }
}
