using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>牆體水壓損傷（設計稿 §5.1）。</summary>
    public static class Tests_WallPressure
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>
        /// P4：冰洋測試地圖上蓋一間一般鋼牆的密閉艙室並抽乾，暫時把地圖的生態區換成海床平原（深海）。
        /// 檢查：牆的兩側是外海與乾艙、厚度 1、承受負載並掉血；外面再貼一圈牆後厚度變 2；
        /// 換成水密隔牆的那一格不受損；淺海時任何牆都不受損。最後還原生態區。
        /// </summary>
        public static IEnumerable<Wait> P4_WallPressure(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_WallPressure wp = map.GetComponent<MapComponent_WallPressure>();
            ctx.Require(wp != null, "every map has the wall pressure component");
            Tests_Flooding.BuildCompartment(ctx, out CellRect inner, out List<Thing> walls);
            foreach (Wait w in Tests_Flooding.DrainCompartment(ctx, inner)) { yield return w; }
            ctx.Check(Tests_Flooding.Level(inner.CenterCell) <= 0.01f, "the compartment is dry");

            Thing mid = walls.First(w => w.Position.z == inner.minZ - 1 && w.Position.x == inner.CenterCell.x);
            IntVec3 midCell = mid.Position;

            // 淺海：水壓 1，一般牆撐得住
            wp.MarkDirty();
            wp.UpdateNow();
            ctx.Check(wp.TryGetEntry(midCell, out WallPressureTopology.Entry shallowEntry) && shallowEntry.thickness == 1, $"the wall separates the sea from the compartment, 1 cell thick ({wp.CachedEntryCount} wall cells cached)");
            ctx.Check(wp.LastLoadedCount > 0 && wp.LastOverloadedCount == 0, $"in the shallows walls carry load but are not overloaded ({wp.LastLoadedCount} loaded, {wp.LastOverloadedCount} overloaded)");

            // 換成深海
            PlanetTile tile = map.Tile;
            BiomeDef original = tile.Tile.PrimaryBiome;
            tile.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("DepthRim_SeabedPlain");
            ctx.Check(DepthRimFixture.Flooding.DepthTier == DepthTier.Deep, $"the test map is now deep ({DepthRimFixture.Flooding.DepthTier})");
            int hp0 = mid.HitPoints;
            for (int i = 0; i < 6; i++) { wp.UpdateNow(); }
            ctx.Check(wp.LastOverloadedCount > 0, $"in the deep, plain walls facing the sea are overloaded ({wp.LastOverloadedCount})");
            ctx.Check(mid.HitPoints < hp0, $"a plain wall is crushed slowly ({hp0} -> {mid.HitPoints})");

            // 一格換成水密隔牆：不受損
            Thing hull = walls.First(w => w.Position.z == inner.maxZ + 1 && w.Position.x == inner.CenterCell.x);
            IntVec3 hullCell = hull.Position;
            // 直接生成在舊牆上（原版會在同一步把舊牆換掉），中間艙室不會漏水
            Thing pressureWall = DepthRimFixture.Spawn(ThingDef.Named("DepthRim_PressureHullWall"), hullCell, map);
            yield return Updates(1);
            // 被壓傷的一般牆開始滲水（既有的滲水系統）：壓力損傷和進水接得起來
            ctx.Check(Tests_Flooding.Level(inner.CenterCell) > 0f, $"walls damaged by pressure let water in ({Tests_Flooding.Level(inner.CenterCell):P1})");
            int hullHp = pressureWall.HitPoints;
            for (int i = 0; i < 6; i++) { wp.UpdateNow(); }
            ctx.Check(pressureWall.HitPoints == hullHp, $"a pressure hull wall holds in the deep ({hullHp} -> {pressureWall.HitPoints})");

            // 外面再貼一圈：同一段牆變兩格厚
            CellRect ring = inner.ExpandedBy(1);
            List<Thing> outer = new List<Thing>();
            foreach (IntVec3 c in ring.ExpandedBy(1).EdgeCells)
            {
                if (c.GetEdifice(map) == null)
                {
                    outer.Add(DepthRimFixture.Spawn(ThingDefOf.Wall, c, map, ThingDefOf.Steel));
                }
            }
            yield return new WaitTicks(5);
            wp.UpdateNow();
            ctx.Check(wp.RebuildCount > 0, $"building walls rebuilt the topology ({wp.RebuildCount} rebuilds)");
            ctx.Check(wp.TryGetEntry(midCell, out WallPressureTopology.Entry thick) && thick.thickness == 2, $"with a second layer the wall is 2 cells thick ({thick.thickness})");

            // 還原
            tile.Tile.PrimaryBiome = original;
            foreach (Thing t in outer) { if (!t.Destroyed) { t.Destroy(); } }
            yield return Updates(1);
            ctx.Check(DepthRimFixture.Flooding.DepthTier == DepthTier.Shallow, "biome restored");
        }
    }
}
