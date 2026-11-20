using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>潛艇船體平台（DepthRim_HullPlatform）：放置規則、邊緣、能蓋船體、拆回深淵。</summary>
    public static class Tests_HullPlatform
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>
        /// H1：在冰洋測試地圖上以深海層級挖出深淵，找一塊 7×5 全是已探索深淵的區域鋪上平台（直接換地形，等同蓋好），
        /// 檢查：只能放在深淵上、平台周圍畫出邊緣、水密隔牆與水密艙門能蓋在平台上、DMSE 的拆除會改回深淵。最後還原地形。
        /// </summary>
        public static IEnumerable<Wait> H1_HullPlatform(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            TerrainDef abyss = DepthRimDefOf.DepthRim_Abyss;
            ThingDef hullWall = ThingDef.Named("DepthRim_PressureHullWall");

            Dictionary<IntVec3, TerrainDef> before = map.AllCells.ToDictionary(c => c, c => map.terrainGrid.TerrainAt(c));
            int chunksBefore = map.listerThings.AllThings.Count(t => t.def.thingCategories?.Contains(ThingCategoryDefOf.Chunks) == true);
            GenStep_AbyssCarve.Carve(map, DepthTier.Deep, 10f, 20f, 0.1f);
            // 深淵吞掉上面的東西：挖洞前散在海床上的石塊不會留在深淵上
            List<Thing> leftOnAbyss = map.listerThings.AllThings.Where(t => t.Spawned && Patch_AbyssSwallowsItems.Sinks(t) && map.terrainGrid.TerrainAt(t.Position) == abyss).ToList();
            ctx.Check(leftOnAbyss.Count == 0, $"no item, chunk or plant is left lying on the abyss ({leftOnAbyss.Count}: {string.Join(", ", leftOnAbyss.Take(3).Select(t => t.def.defName))})");
            int chunksAfter = map.listerThings.AllThings.Count(t => t.def.thingCategories?.Contains(ThingCategoryDefOf.Chunks) == true);
            ctx.Note($"chunks on the map: {chunksBefore} before the carve, {chunksAfter} after");

            // 7×5 的區域：內部鋪平台，外圈一格還是深淵（邊緣才畫得出來）
            CellRect area = CellRect.Empty;
            foreach (IntVec3 c in map.AllCells)
            {
                // 南邊多留三排深淵：平台下方的側面（Loop 貼圖，高兩格）掛在那裡；離地圖邊緣遠一點，截圖才拍得到
                CellRect r = new CellRect(c.x, c.z, 7, 5);
                CellRect around = CellRect.FromLimits(r.minX - 1, r.minZ - 3, r.maxX + 1, r.maxZ + 1);
                if (r.minZ >= 12 && r.minX >= 12 && r.maxX < map.Size.x - 12 && r.maxZ < map.Size.z - 12
                    && around.Cells.All(n => map.terrainGrid.TerrainAt(n) == abyss && !n.Fogged(map)))
                {
                    area = r;
                    break;
                }
            }
            ctx.Require(area.Area > 0, "found a 9x9 patch of explored abyss away from the map edge");
            ctx.Note($"platform area {area}");

            // 之後才變成深淵的格子也一樣：在一格深淵上硬放一疊鋼材，再把它設回深淵，鋼材沉下去
            IntVec3 sinkCell = new IntVec3(area.minX - 1, 0, area.minZ - 2);
            Thing dropped = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), sinkCell, map);
            map.terrainGrid.SetTerrain(sinkCell, abyss);
            ctx.Check(dropped.Destroyed, "an item on a cell that becomes abyss sinks");

            IntVec3 seabed = map.AllCells.First(c => !c.Fogged(map) && c.Standable(map) && map.terrainGrid.TerrainAt(c) != abyss && c.GetEdifice(map) == null);
            // 建築分頁「水下」真的列出 DepthRim 的建築與拆除平台工具
            DesignationCategoryDef tab = DefDatabase<DesignationCategoryDef>.GetNamed("DepthRim_Underwater");
            List<Designator> designators = tab.AllResolvedDesignators;
            foreach (string name in new[] { "DepthRim_IceShaft", "DepthRim_BilgePump", "DepthRim_WatertightDoor", "DepthRim_PressureHullWall", "DepthRim_HullPlatform" })
            {
                ctx.Check(designators.OfType<Designator_Build>().Any(d => d.PlacingDef.defName == name), $"the underwater tab lists {name}");
            }
            ctx.Check(designators.Any(d => d is Designator_RemovePlatform), "the underwater tab has DMSE's remove-platform tool");

            ctx.Check(GenConstruct.CanPlaceBlueprintAt(platform, area.CenterCell, Rot4.North, map).Accepted, "the platform can be laid over the abyss");
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(platform, seabed, Rot4.North, map).Accepted, "the platform can also be laid on the seabed");

            foreach (IntVec3 c in area)
            {
                map.terrainGrid.SetTerrain(c, platform);
            }
            yield return new WaitFrames(5);
            IntVec3 inner = area.CenterCell;
            IntVec3 rim = new IntVec3(area.minX, 0, area.minZ + 2);
            ctx.Check(area.Cells.All(c => c.Standable(map)), "every platform cell is walkable (no chunk was left on the abyss underneath)");
            ctx.Check(map.terrainGrid.BaseTerrainAt(inner) == platform, "the platform is base terrain");

            // 邊框與側面：SectionLayer_HullPlatformProps（比照 SubstructureProps），不是原版 TerrainEdges
            SectionLayer_HullPlatformProps PropsLayer(IntVec3 c)
            {
                SectionLayer_HullPlatformProps l = (SectionLayer_HullPlatformProps)map.mapDrawer.SectionAt(c).GetLayer(typeof(SectionLayer_HullPlatformProps));
                l.Regenerate();
                return l;
            }
            SectionLayer_HullPlatformProps rimLayer = PropsLayer(rim);
            int borderVerts = rimLayer.subMeshes.Where(s => s.verts != null).Sum(s => s.verts.Count);
            ctx.Check(borderVerts > 0, $"platform borders are drawn ({borderVerts} vertices)");
            IntVec3 below = new IntVec3(area.CenterCell.x, 0, area.minZ - 1);
            SectionLayer_HullPlatformProps belowLayer = PropsLayer(new IntVec3(area.CenterCell.x, 0, area.minZ));
            ctx.Check(belowLayer.lastSides.Any(t => t.cell == below && t.height == 2), "a two-cell side face hangs below the platform over open abyss");
            SectionLayer vanillaEdges = map.mapDrawer.SectionAt(below).GetLayer(typeof(SectionLayer_TerrainEdges));
            vanillaEdges.Regenerate();
            HashSet<string> platformTex = new HashSet<string>(platform.GetModExtension<HullPlatformPropsExtension>().AllTexPaths().Select(p => p.Split('/').Last()));
            ctx.Check(!vanillaEdges.subMeshes.Any(s => s.verts != null && s.verts.Count > 0 && s.material?.mainTexture != null && platformTex.Contains(s.material.mainTexture.name)),
                "vanilla TerrainEdges draws nothing for the platform");

            // 第二格是實地：只畫上半，不蓋在海床上
            IntVec3 solid = new IntVec3(area.CenterCell.x, 0, area.minZ - 2);
            TerrainDef seabedTerrain = map.terrainGrid.TerrainAt(map.AllCells.First(c => map.terrainGrid.TerrainAt(c)?.spaceEdgeGraphicData != null));
            map.terrainGrid.SetTerrain(solid, seabedTerrain);
            belowLayer = PropsLayer(new IntVec3(area.CenterCell.x, 0, area.minZ));
            ctx.Check(belowLayer.lastSides.Any(t => t.cell == below && t.height == 1), "with seabed two cells down only the top half is drawn");
            foreach (Wait w in ctx.ScreenshotOn(map, below, 5f, "hull_platform_side_clipped", 40)) { yield return w; }
            yield return new WaitFrames(30);
            map.terrainGrid.SetTerrain(solid, abyss);

            MapComponent_Midwater midwater = map.GetComponent<MapComponent_Midwater>();
            Texture2D mask = midwater.RebuildMask();
            ctx.Check(mask.GetPixels32()[(area.minZ - 1) * map.Size.x + below.x].r == 0, "midwater creatures never cover the side faces (mask is clear under the platform edge)");
            ctx.Check(mask.GetPixels32()[(area.minZ - 3) * map.Size.x + below.x].r == 255, "but the open abyss further down is still in the mask");

            // 船體：水密隔牆與水密艙門能蓋在平台上
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(hullWall, rim, Rot4.North, map).Accepted, "a pressure hull wall can be built on the platform");
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(DepthRimDefOf.DepthRim_WatertightDoor, new IntVec3(area.maxX, 0, area.minZ + 2), Rot4.East, map).Accepted, "a watertight door can be built on the platform");

            // 截圖：平台浮在深淵上（看下垂側面）；另外拍一段樓梯狀的海床崖邊（側面不能浮在海床上）
            foreach (Wait w in ctx.ScreenshotOn(map, inner, 8f, "hull_platform", 40)) { yield return w; }
            yield return new WaitFrames(30);
            IntVec3 stair = map.AllCells.FirstOrDefault(c => !c.Fogged(map) && map.terrainGrid.TerrainAt(c) == abyss
                && map.terrainGrid.TerrainAt(c + IntVec3.North)?.spaceEdgeGraphicData != null && map.terrainGrid.TerrainAt(c + IntVec3.North) != platform
                && (c + IntVec3.South).InBounds(map) && map.terrainGrid.TerrainAt(c + IntVec3.South)?.dontRender == false);
            if (stair.IsValid)
            {
                ctx.Note($"stair-shaped cliff at {stair}");
                foreach (Wait w in ctx.ScreenshotOn(map, stair, 6f, "abyss_cliff_stair", 40)) { yield return w; }
                yield return new WaitFrames(30);
            }

            // 拆除：DMSE 的拆除改回太空，DepthRim 在海洋層改回深淵
            ctx.Check(PlatformRemovalUtility.CanRemoveAt(rim, map, out string reason), $"DMSE's remove-platform command accepts it ({reason})");
            PlatformRemovalUtility.Remove(rim, map, doLeavings: true);
            ctx.Check(map.terrainGrid.TerrainAt(rim) == abyss, $"removal leaves the abyss, not space ({map.terrainGrid.TerrainAt(rim)?.defName})");
            ctx.Check(!map.AllCells.Any(c => map.terrainGrid.TerrainAt(c) == TerrainDefOf.Space), "no space terrain anywhere on the ocean map");
            List<Thing> steel = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Position.InHorDistOf(rim, 8f)).ToList();
            ctx.Check(steel.Sum(t => t.stackCount) >= 10, $"half the steel is refunded nearby ({steel.Sum(t => t.stackCount)})");

            // 還原
            foreach (Thing t in steel) { t.Destroy(); }
            foreach (KeyValuePair<IntVec3, TerrainDef> kv in before)
            {
                if (map.terrainGrid.TerrainAt(kv.Key) != kv.Value)
                {
                    map.terrainGrid.SetTerrain(kv.Key, kv.Value);
                }
            }
            yield return Updates(1);
            ctx.Check(map.AllCells.All(c => map.terrainGrid.TerrainAt(c) == before[c]), "terrain restored after the test");
        }

        /// <summary>
        /// H4：船體平台鋪在海床上：能放、被蓋掉的海床記在 MapComponent_SeabedMemory，
        /// 南緣緊鄰海床時側面和海面一樣只露出乾舷（半格），水密隔牆蓋得上去，DMSE 拆除後海床回來（不留深淵洞）。
        /// </summary>
        public static IEnumerable<Wait> H4_HullOnSeabed(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            TerrainDef abyss = DepthRimDefOf.DepthRim_Abyss;
            ThingDef hullWall = ThingDef.Named("DepthRim_PressureHullWall");
            float freeboard = platform.GetModExtension<HullPlatformPropsExtension>().seaSurfaceFreeboard;

            bool Seabed(IntVec3 c) => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                && map.terrainGrid.TerrainAt(c) is TerrainDef t && !t.dontRender && t != abyss && !MapComponent_SeabedMemory.IsPlatform(t)
                && t.affordances.Contains(platform.terrainAffordanceNeeded);
            CellRect area = CellRect.Empty;
            foreach (IntVec3 c in map.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 5, 3);
                CellRect around = CellRect.FromLimits(r.minX - 1, r.minZ - 2, r.maxX + 1, r.maxZ + 1);
                if (r.minZ >= 12 && r.minX >= 12 && r.maxX < map.Size.x - 12 && r.maxZ < map.Size.z - 12 && around.Cells.All(Seabed))
                {
                    area = r;
                    break;
                }
            }
            ctx.Require(area.Area > 0, "found a patch of open seabed away from the map edge");
            ctx.Note($"seabed platform area {area}");
            Dictionary<IntVec3, TerrainDef> before = area.Cells.ToDictionary(c => c, c => map.terrainGrid.TerrainAt(c));

            ctx.Check(GenConstruct.CanPlaceBlueprintAt(platform, area.CenterCell, Rot4.North, map).Accepted, "the platform can be laid on the seabed");
            foreach (IntVec3 c in area)
            {
                map.terrainGrid.SetTerrain(c, platform);
            }
            yield return new WaitFrames(5);
            MapComponent_SeabedMemory memory = map.GetComponent<MapComponent_SeabedMemory>();
            ctx.Check(area.Cells.All(c => memory.TryGetUnder(c, out TerrainDef u) && u == before[c]), "the covered seabed is remembered under every platform cell");
            ctx.Check(area.Cells.All(c => c.Standable(map)), "every platform cell is walkable");

            IntVec3 edge = new IntVec3(area.CenterCell.x, 0, area.minZ);
            IntVec3 below = edge + IntVec3.South;
            SectionLayer_HullPlatformProps layer = (SectionLayer_HullPlatformProps)map.mapDrawer.SectionAt(edge).GetLayer(typeof(SectionLayer_HullPlatformProps));
            layer.Regenerate();
            ctx.Check(SectionLayer_HullPlatformProps.RestsOnSeabed(edge, below, map), "the platform rests on the seabed");
            ctx.Check(layer.lastSides.Any(t => t.cell == below && Mathf.Abs(t.height - freeboard) < 0.01f),
                $"on the seabed only the freeboard ({freeboard} cell) of the side shows, like on the sea surface ({string.Join(", ", layer.lastSides.Where(t => t.cell.x == below.x).Select(t => $"{t.cell}:{t.height}"))})");
            ctx.Check(!layer.lastSides.Any(t => t.height >= 1f && area.Contains(t.cell + IntVec3.North)), "no full-height side face is drawn over the seabed");

            ctx.Check(GenConstruct.CanPlaceBlueprintAt(hullWall, new IntVec3(area.minX, 0, area.minZ + 1), Rot4.North, map).Accepted, "a pressure hull wall can be built on the seabed platform");
            foreach (Wait w in ctx.ScreenshotOn(map, area.CenterCell, 6f, "hull_platform_on_seabed", 40)) { yield return w; }
            yield return new WaitFrames(30);

            IntVec3 rim = new IntVec3(area.minX, 0, area.minZ + 1);
            ctx.Check(PlatformRemovalUtility.CanRemoveAt(rim, map, out string reason), $"DMSE's remove-platform command accepts it ({reason})");
            PlatformRemovalUtility.Remove(rim, map, doLeavings: true);
            ctx.Check(map.terrainGrid.TerrainAt(rim) == before[rim], $"removal brings the seabed back, not an abyss hole ({map.terrainGrid.TerrainAt(rim)?.defName})");
            ctx.Check(!memory.TryGetUnder(rim, out _), "the record is cleared once the seabed is back");

            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Position.InHorDistOf(rim, 8f)).ToList()) { t.Destroy(); }
            foreach (KeyValuePair<IntVec3, TerrainDef> kv in before)
            {
                if (map.terrainGrid.TerrainAt(kv.Key) != kv.Value)
                {
                    map.terrainGrid.SetTerrain(kv.Key, abyss);
                }
            }
            yield return Updates(1);
            ctx.Check(area.Cells.All(c => map.terrainGrid.TerrainAt(c) == before[c]) && area.Cells.All(c => !memory.TryGetUnder(c, out _)), "terrain restored after the test");
        }
            /// <summary>
        /// H5：抗壓水密牆的斜角（SectionLayer_ShipHullCorners）：在海床上鋪一塊平台，蓋一圈帶兩段斜接（橫牆斜接直牆、直牆斜接直牆）的牆，
        /// 斜接的四個缺口格各貼一張 ShipHull 風格的斜角圖（室內的直角內角不補），直牆往下接著時用 _Linked 變體；截圖後還原。
        /// </summary>
        public static IEnumerable<Wait> H5_HullAngledCorners(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            TerrainDef abyss = DepthRimDefOf.DepthRim_Abyss;
            ThingDef hullWall = ThingDef.Named("DepthRim_PressureHullWall");

            bool Seabed(IntVec3 c) => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                && map.terrainGrid.TerrainAt(c) is TerrainDef t && !t.dontRender && t != abyss && !MapComponent_SeabedMemory.IsPlatform(t)
                && t.affordances.Contains(platform.terrainAffordanceNeeded);
            CellRect area = CellRect.Empty;
            foreach (IntVec3 c in map.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 8, 7);
                if (r.minZ >= 12 && r.minX >= 12 && r.maxX < map.Size.x - 12 && r.maxZ < map.Size.z - 12 && r.ExpandedBy(1).Cells.All(Seabed))
                {
                    area = r;
                    break;
                }
            }
            ctx.Require(area.Area > 0, "found a patch of open seabed away from the map edge");
            Dictionary<IntVec3, TerrainDef> before = area.Cells.ToDictionary(c => c, c => map.terrainGrid.TerrainAt(c));
            foreach (IntVec3 c in area)
            {
                map.terrainGrid.SetTerrain(c, platform);
            }
            // 相對 area 左下角：上緣橫牆 x0–4 在 z6，(5,5) 斜接、往下直牆到 z3，(6,2) 再斜接、往下到 z0，底牆 x0–6，左牆 x0
            IntVec3 o = new IntVec3(area.minX, 0, area.minZ);
            List<IntVec3> cells = new List<IntVec3>();
            for (int x = 0; x <= 4; x++) { cells.Add(new IntVec3(x, 0, 6)); }
            for (int z = 3; z <= 5; z++) { cells.Add(new IntVec3(5, 0, z)); }
            for (int z = 1; z <= 2; z++) { cells.Add(new IntVec3(6, 0, z)); }
            for (int x = 0; x <= 6; x++) { cells.Add(new IntVec3(x, 0, 0)); }
            for (int z = 1; z <= 5; z++) { cells.Add(new IntVec3(0, 0, z)); }
            List<Thing> walls = new List<Thing>();
            foreach (IntVec3 rel in cells.Distinct())
            {
                walls.Add(GenSpawn.Spawn(ThingMaker.MakeThing(hullWall), o + rel, map));
            }
            yield return new WaitFrames(5);

            Dictionary<string, int> quads = new Dictionary<string, int>();
            // 斜角貼圖 2×2 格、可能跨區塊：把這塊地蓋到的每個區塊都重建一次再數
            foreach (Section section in area.ExpandedBy(1).Cells.Select(c => map.mapDrawer.SectionAt(c)).Distinct())
            {
                SectionLayer layer = section.GetLayer(typeof(SectionLayer_ShipHullCorners));
                layer.Regenerate();
                foreach (LayerSubMesh sm in layer.subMeshes)
                {
                    string name = sm.material?.mainTexture?.name ?? "null";
                    if (sm.verts.Count > 0 && sm.material.shader == ShaderDatabase.CutoutOverlay)
                    {
                        quads[name] = (quads.TryGetValue(name, out int n) ? n : 0) + sm.verts.Count / 4;
                    }
                }
            }
            ctx.Note("angled pieces: " + string.Join(", ", quads.Select(kv => $"{kv.Key}×{kv.Value}")));
            ctx.Check(quads.Values.Sum() == 4, $"the two diagonal joints get four angled pieces, the room's square inner corners none ({quads.Values.Sum()})");
            ShipHullAngledExtension ext = hullWall.GetModExtension<ShipHullAngledExtension>();
            string prefix = ext.texPathPrefix.Substring(ext.texPathPrefix.LastIndexOf('/') + 1);
            ctx.Note($"corner texture set: {ext.texPathPrefix} (variants {ext.lowerVariants})");
            ctx.Check(quads.Keys.All(k => k.StartsWith(prefix + "Partial_")), "every piece is a diagonal (partial) piece from the wall's texture set");
            if (ext.lowerVariants)
            {
                ctx.Check(quads.ContainsKey(prefix + "Partial_northeast_Linked"), "the gap under the top wall uses the variant for a wall that continues south");
            }
            ctx.Check(quads.ContainsKey(prefix + "Partial_southwest"), "the outer gap of the joint gets the south-west piece");
            // 水體會把整片蓋成深藍，截圖時先關掉水體顯示
            bool showWater = FloodingOverlay.Show;
            FloodingOverlay.Show = false;
            foreach (Wait w in ctx.ScreenshotOn(map, area.CenterCell, 4.5f, "hull_angled_corners", 40)) { yield return w; }
            yield return new WaitFrames(30);
            FloodingOverlay.Show = showWater;

            foreach (Thing t in walls) { if (!t.Destroyed) { t.Destroy(); } }
            foreach (KeyValuePair<IntVec3, TerrainDef> kv in before)
            {
                map.terrainGrid.SetTerrain(kv.Key, kv.Value);
            }
            yield return Updates(1);
        }

        /// <summary>
        /// H6（海岸造船 M0）：原版地表殖民地上挖一塊原版海水（深海水，中間夾一格淺海水），鋪船體平台；
        /// 檢查：原版深／淺海水帶 DepthRim_HullBase、河川深水不帶；平台記下原本的水；南邊是水就只畫乾舷、貼著陸地不畫；
        /// DMSE 的拆除平台與船體分離（鋪太空）都還原成原本的水，不留太空；沒有記錄的格退回原版深海水。最後還原地形。
        /// </summary>
        public static IEnumerable<Wait> H6_HullOnVanillaOcean(TestContext ctx)
        {
            Map map = DepthRimFixture.Home;
            yield return new WaitFrames(2);
            TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            TerrainDef deep = TerrainDefOf.WaterOceanDeep;
            TerrainDef shallow = TerrainDefOf.WaterOceanShallow;
            TerrainAffordanceDef hullBase = platform.terrainAffordanceNeeded;
            float freeboard = platform.GetModExtension<HullPlatformPropsExtension>().seaSurfaceFreeboard;

            ctx.Check(deep.affordances.Contains(hullBase) && shallow.affordances.Contains(hullBase), "vanilla deep and shallow ocean water carry the hull-base affordance");
            ctx.Check(!TerrainDefOf.WaterDeep.affordances.Contains(hullBase) && !TerrainDefOf.WaterShallow.affordances.Contains(hullBase), "river and fresh water do not");
            ctx.Check(shallow.affordances.Contains(TerrainAffordanceDefOf.Bridgeable), "shallow ocean water keeps its vanilla affordances");
            ctx.Check(SeaLevels.IsVanillaSurface(map) && SeaLevels.ReplacesSpace(map), "the home map counts as a vanilla surface map");
            ctx.Check(SeaLevels.VoidTerrain(map) == deep, $"its void terrain is vanilla deep ocean water ({SeaLevels.VoidTerrain(map)?.defName})");
            if (DepthRimFixture.Ocean != null)
            {
                ctx.Check(!SeaLevels.IsVanillaSurface(DepthRimFixture.Ocean), "an ocean-layer map is not a vanilla surface map");
            }

            bool Free(IntVec3 c) => c.InBounds(map) && !c.Fogged(map) && c.GetThingList(map).Count == 0 && map.terrainGrid.UnderTerrainAt(c) == null
                && map.terrainGrid.TerrainAt(c) is TerrainDef t && !t.IsWater && !MapComponent_SeabedMemory.IsPlatform(t);
            CellRect area = CellRect.Empty;
            foreach (IntVec3 c in map.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 5, 3);
                if (r.minZ >= 12 && r.minX >= 12 && r.maxX < map.Size.x - 12 && r.maxZ < map.Size.z - 12 && r.ExpandedBy(2).Cells.All(Free))
                {
                    area = r;
                    break;
                }
            }
            ctx.Require(area.Area > 0, "found a clear patch of land on the home map");
            CellRect water = area.ExpandedBy(2);
            Dictionary<IntVec3, TerrainDef> original = water.Cells.ToDictionary(c => c, c => map.terrainGrid.TerrainAt(c));
            IntVec3 shoreBelow = new IntVec3(area.minX, 0, area.minZ - 1); // 西南角下面留一格陸地（貼岸）
            IntVec3 shallowCell = new IntVec3(area.maxX, 0, area.maxZ);
            foreach (IntVec3 c in water)
            {
                if (c != shoreBelow)
                {
                    map.terrainGrid.SetTerrain(c, c == shallowCell ? shallow : deep);
                }
            }
            Dictionary<IntVec3, TerrainDef> wet = area.Cells.ToDictionary(c => c, c => map.terrainGrid.TerrainAt(c));
            ctx.Note($"vanilla ocean platform area {area}");

            // 河川深水的放置只看承載力（上面已檢查）：不在這裡把水格換成別種水，原版 WaterBodyTracker 處理不了水→水的切換
            ctx.Check(deep.changeable, "vanilla deep ocean water is changeable, so terrain blueprints are not refused outright");
            AcceptanceReport onDeep = GenConstruct.CanPlaceBlueprintAt(platform, area.CenterCell, Rot4.North, map);
            ctx.Check(onDeep.Accepted, $"the platform can be laid on vanilla deep ocean water ({onDeep.Reason})");
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(platform, shallowCell, Rot4.North, map).Accepted, "and on vanilla shallow ocean water");

            foreach (IntVec3 c in area)
            {
                map.terrainGrid.SetTerrain(c, platform);
            }
            yield return new WaitFrames(5);
            MapComponent_SeabedMemory memory = map.GetComponent<MapComponent_SeabedMemory>();
            ctx.Check(area.Cells.All(c => memory.TryGetUnder(c, out TerrainDef u) && u == wet[c]), "the covered water is remembered under every platform cell (deep and shallow)");
            ctx.Check(area.Cells.All(c => c.Standable(map)), "every platform cell is walkable");

            IntVec3 centerEdge = new IntVec3(area.CenterCell.x, 0, area.minZ);
            IntVec3 shoreEdge = new IntVec3(area.minX, 0, area.minZ);
            SectionLayer_HullPlatformProps layer = (SectionLayer_HullPlatformProps)map.mapDrawer.SectionAt(centerEdge).GetLayer(typeof(SectionLayer_HullPlatformProps));
            layer.Regenerate();
            if (map.mapDrawer.SectionAt(shoreEdge) != map.mapDrawer.SectionAt(centerEdge))
            {
                ((SectionLayer_HullPlatformProps)map.mapDrawer.SectionAt(shoreEdge).GetLayer(typeof(SectionLayer_HullPlatformProps))).Regenerate();
            }
            ctx.Check(layer.lastSides.Any(t => t.cell == centerEdge + IntVec3.South && Mathf.Abs(t.height - freeboard) < 0.01f),
                $"over vanilla water only the freeboard ({freeboard} cell) of the side shows ({string.Join(", ", layer.lastSides.Select(t => $"{t.cell}:{t.height}"))})");
            ctx.Check(!layer.lastSides.Any(t => t.cell == shoreBelow), "no side face is drawn onto the land the platform touches");
            ctx.Check(!layer.lastSides.Any(t => t.height >= 1f), "no full-height side face hangs over vanilla water");
            foreach (Wait w in ctx.ScreenshotOn(map, area.CenterCell, 6f, "hull_platform_on_vanilla_ocean", 40)) { yield return w; }
            yield return new WaitFrames(30);

            IntVec3 rim = new IntVec3(area.minX, 0, area.minZ + 1);
            ctx.Check(PlatformRemovalUtility.CanRemoveAt(rim, map, out string reason), $"DMSE's remove-platform command accepts it ({reason})");
            PlatformRemovalUtility.Remove(rim, map, doLeavings: true);
            ctx.Check(map.terrainGrid.TerrainAt(rim) == wet[rim], $"removal brings the vanilla water back, not space ({map.terrainGrid.TerrainAt(rim)?.defName})");
            ctx.Check(!memory.TryGetUnder(rim, out _), "the record is cleared once the water is back");

            // DMSE 的船體分離在地表圖層上把船體範圍鋪成太空：每一格都要還原成原本的水
            foreach (IntVec3 c in area.Cells.Where(c => MapComponent_SeabedMemory.IsPlatform(map.terrainGrid.TerrainAt(c))).ToList())
            {
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Space);
            }
            ctx.Check(area.Cells.All(c => map.terrainGrid.TerrainAt(c) == wet[c]),
                $"hull separation (space) restores the original water under every cell ({string.Join(", ", area.Cells.Where(c => map.terrainGrid.TerrainAt(c) != wet[c]).Select(c => $"{c}:{map.terrainGrid.TerrainAt(c)?.defName}"))})");
            ctx.Check(map.terrainGrid.TerrainAt(shallowCell) == shallow, "the shallow cell comes back shallow");
            ctx.Check(area.Cells.All(c => !memory.TryGetUnder(c, out _)), "all records are used up");

            IntVec3 bare = area.CenterCell;
            map.terrainGrid.SetTerrain(bare, platform);
            memory.Forget(bare);
            map.terrainGrid.SetTerrain(bare, TerrainDefOf.Space);
            ctx.Check(map.terrainGrid.TerrainAt(bare) == deep, $"a platform cell without a record falls back to vanilla deep ocean water ({map.terrainGrid.TerrainAt(bare)?.defName})");
            IntVec3 land = water.Cells.First(c => original[c] == map.terrainGrid.TerrainAt(c)); // 留著的那格陸地
            map.terrainGrid.SetTerrain(land, TerrainDefOf.Space);
            ctx.Check(map.terrainGrid.TerrainAt(land) == TerrainDefOf.Space, "space that did not come from a hull platform is left alone on a vanilla map");

            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Position.InHorDistOf(rim, 8f)).ToList()) { t.Destroy(); }
            foreach (KeyValuePair<IntVec3, TerrainDef> kv in original)
            {
                map.terrainGrid.SetTerrain(kv.Key, kv.Value);
            }
            yield return new WaitFrames(2);
            ctx.Check(water.Cells.All(c => map.terrainGrid.TerrainAt(c) == original[c]) && water.Cells.All(c => !memory.TryGetUnder(c, out _)), "terrain restored after the test");
        }
    }
}
