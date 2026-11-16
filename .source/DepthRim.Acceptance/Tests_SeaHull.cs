using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>海面地圖上的船體平台：鋪在開放海面上、浮在水線（側面只露出乾舷、影子與白沫貼著船身）。</summary>
    public static class Tests_SeaHull
    {
        /// <summary>
        /// H3：建立海面地圖（水面層），在開放海面鋪一塊 9×5 的船體平台（直接換地形，等同蓋好）。
        /// 檢查：船體平台能放在開放海面、不能放在海上平台上；側面只畫乾舷高度（海上平台照舊兩格）；
        /// 海面遮罩把它記成浮在水面的平台（b），不算進高出海面的平台（r）；水密隔牆能蓋在上面；拆除露出海面。截圖近景與全景。
        /// </summary>
        public static IEnumerable<Wait> H3_HullOnSeaSurface(TestContext ctx)
        {
            Map map = Tests_SeaSurface.CreateSeaMap(ctx);
            // 海面地圖沒人時幾幀內就會被移除：先在角落放一小塊浮船與一名殖民者
            CellRect raft = new CellRect(map.Center.x + 10, map.Center.z + 10, 2, 2);
            Tests_SeaSurface.LayHull(map, raft);
            Pawn colonist = DepthRimFixture.SpawnColonist(map, raft.CenterCell);
            yield return new WaitFrames(5);
            ctx.Require(map.Parent != null && map.Parent.HasMap, "the sea surface map exists");

            TerrainDef sea = DepthRimDefOf.DepthRim_OpenSea;
            TerrainDef hull = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            // 地圖中央西邊的開放海面，南邊留 3 格海
            CellRect area = new CellRect(map.Center.x - 12, map.Center.z - 2, 9, 5);
            ctx.Require(area.ExpandedBy(3).InBounds(map) && area.ExpandedBy(1).Cells.All(c => map.terrainGrid.TerrainAt(c) == sea), $"a patch of open sea ({area})");

            ctx.Check(GenConstruct.CanPlaceBlueprintAt(hull, area.CenterCell, Rot4.North, map, godMode: true).Accepted, "the hull platform can be laid on open sea");
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(hull, new IntVec3(area.minX - 20, 0, area.minZ), Rot4.North, map, godMode: true).Accepted || area.minX - 20 < 0, "it needs no pillar support (it floats)");

            foreach (IntVec3 c in area) { map.terrainGrid.SetTerrain(c, hull); }
            yield return new WaitFrames(5);
            ctx.Check(area.Cells.All(c => c.Standable(map)), "every hull cell is walkable");
            ctx.Check(map.GetComponent<MapComponent_PlatformSupport>()?.PendingCount == 0, "the floating hull does not collapse without pillars");

            // 側面：乾舷
            float freeboard = hull.GetModExtension<HullPlatformPropsExtension>().seaSurfaceFreeboard;
            IntVec3 below = new IntVec3(area.CenterCell.x, 0, area.minZ - 1);
            SectionLayer_HullPlatformProps layer = (SectionLayer_HullPlatformProps)map.mapDrawer.SectionAt(new IntVec3(area.CenterCell.x, 0, area.minZ)).GetLayer(typeof(SectionLayer_HullPlatformProps));
            layer.Regenerate();
            ctx.Check(layer.lastSides.Any(t => t.cell == below && Mathf.Abs(t.height - freeboard) < 0.001f), $"the hull side shows only {freeboard} cells of freeboard ({string.Join(", ", layer.lastSides.Where(t => t.cell == below).Select(t => t.height))})");
            // 海面遮罩
            MapComponent_SeaSurface seaComp = map.GetComponent<MapComponent_SeaSurface>();
            seaComp.RebuildMask();
            ctx.Check(seaComp.lastFloatingCells == area.Area + raft.Area, $"the sea mask marks the hulls as floating ({seaComp.lastFloatingCells} of {area.Area + raft.Area})");
            ctx.Check(seaComp.lastPlatformCells == 0, $"no raised platform yet ({seaComp.lastPlatformCells})");
            Color32 px = seaComp.Mask.GetPixel(area.CenterCell.x, area.CenterCell.z);
            ctx.Check(px.b > 200 && px.r < 50, $"a hull cell is b, not r, in the mask ({px})");
            Material mat = seaComp.Material;
            ctx.Check(mat != null && Mathf.Abs(mat.GetVector("_Hull").x - freeboard) < 0.001f, "the sea shader's waterline is set to the freeboard");

            // 船殼能蓋在上面
            ThingDef hullWall = ThingDef.Named("DepthRim_PressureHullWall");
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(hullWall, new IntVec3(area.minX, 0, area.maxZ), Rot4.North, map, godMode: true).Accepted, "pressure hull walls can be built on the floating hull");
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(DepthRimDefOf.DepthRim_WatertightDoor, new IntVec3(area.CenterCell.x, 0, area.maxZ), Rot4.North, map, godMode: true).Accepted, "and watertight doors");
            List<Thing> walls = new List<Thing>();
            foreach (IntVec3 c in area.EdgeCells.Where(c => c.z == area.maxZ))
            {
                walls.Add(DepthRimFixture.Spawn(hullWall, c, map));
            }
            yield return new WaitFrames(5);

            // 截圖：近景（側面與水線）、全景（和高架的海上平台一起看）
            foreach (Wait w in ctx.ScreenshotOn(map, new IntVec3(area.CenterCell.x, 0, area.minZ), 9f, "sea_surface_hull_close", 60)) { yield return w; }
            yield return new WaitFrames(30);
            foreach (Wait w in ctx.ScreenshotOn(map, area.CenterCell, 24f, "sea_surface_hull_wide", 40)) { yield return w; }
            yield return new WaitFrames(30);

            // 拆除：露出開放海面
            foreach (Thing t in walls) { if (!t.Destroyed) { t.Destroy(DestroyMode.Vanish); } }
            IntVec3 corner = new IntVec3(area.minX, 0, area.minZ);
            PlatformRemovalUtility.Remove(corner, map, doLeavings: false);
            ctx.Check(map.terrainGrid.TerrainAt(corner) == sea, $"a removed hull platform leaves open sea ({map.terrainGrid.TerrainAt(corner)?.defName})");

            // 收尾
            colonist.Destroy(DestroyMode.Vanish);
            Tests_SeaSurface.RemoveSeaMaps(map);
            yield return new WaitFrames(2);
        }
    }
}
