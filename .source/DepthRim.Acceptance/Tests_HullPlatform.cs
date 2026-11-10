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
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(platform, seabed, Rot4.North, map).Accepted, "the platform cannot be laid on the seabed");

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
    }
}
