using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 沉船地點：海床上一艘斷成幾截的鋼船殼。外殼有破口（照 <see cref="MapComponent_Flooding"/> 的規則整艘泡在水裡），
    /// 內部用隔牆分成幾個艙，屋頂殘缺；每個艙放一點戰利品，四周散落鋼渣。
    /// 位置挑地圖中央附近、整塊都是海床（不是深淵、沒有岩體）的地方；找不到就清掉岩體硬放。
    /// </summary>
    public class GenStep_Shipwreck : GenStep
    {
        public IntRange length = new IntRange(22, 30);

        public IntRange width = new IntRange(8, 10);

        public IntRange compartments = new IntRange(3, 4);

        /// <summary>外殼牆每格缺損的機率。</summary>
        public float breachChance = 0.12f;

        /// <summary>內部每格有屋頂的機率。</summary>
        public float roofChance = 0.6f;

        public IntRange slagChunks = new IntRange(8, 14);

        /// <summary>每個艙各擲一次的戰利品。</summary>
        public List<ThingDefCountRangeClass> loot = new List<ThingDefCountRangeClass>();

        public override int SeedPart => 734912058;

        /// <summary>最後一次生成的船殼範圍（測試用）。</summary>
        public static CellRect lastHull = CellRect.Empty;

        public override void Generate(Map map, GenStepParams parms)
        {
            bool horizontal = Rand.Bool;
            int len = length.RandomInRange;
            int wid = width.RandomInRange;
            int w = horizontal ? len : wid;
            int h = horizontal ? wid : len;
            CellRect hull = FindSpot(map, w, h);
            lastHull = hull;
            ThingDef stuff = ThingDefOf.Steel;

            // 清掉範圍內與周圍一格的岩體與植物，地形換成海床（深淵上的換回周圍常見的海床地形）
            TerrainDef floor = SeabedTerrain(map, hull);
            foreach (IntVec3 c in hull.ExpandedBy(1).ClipInsideMap(map))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && (t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Plant))
                    {
                        t.Destroy();
                    }
                }
                if (map.terrainGrid.TerrainAt(c) == DepthRimDefOf.DepthRim_Abyss && floor != null)
                {
                    map.terrainGrid.SetTerrain(c, floor);
                }
                map.roofGrid.SetRoof(c, null);
            }

            // 外殼
            foreach (IntVec3 c in hull.EdgeCells)
            {
                if (!Rand.Chance(breachChance))
                {
                    PlaceWall(map, c, stuff);
                }
            }
            // 至少一個開口：從船尾切開
            IntVec3 stern = horizontal ? new IntVec3(hull.minX, 0, hull.CenterCell.z) : new IntVec3(hull.CenterCell.x, 0, hull.minZ);
            ClearWall(map, stern);

            // 隔牆：把長邊切成幾段，每道隔牆留一格通道
            int count = compartments.RandomInRange;
            List<CellRect> rooms = new List<CellRect>();
            int span = (horizontal ? hull.Width : hull.Height) - 2;
            int start = horizontal ? hull.minX + 1 : hull.minZ + 1;
            int prev = start;
            for (int i = 1; i <= count; i++)
            {
                int cut = i == count ? start + span : start + span * i / count;
                CellRect room = horizontal
                    ? CellRect.FromLimits(prev, hull.minZ + 1, cut - 1, hull.maxZ - 1)
                    : CellRect.FromLimits(hull.minX + 1, prev, hull.maxX - 1, cut - 1);
                rooms.Add(room);
                if (i < count)
                {
                    int gap = horizontal ? Rand.RangeInclusive(hull.minZ + 1, hull.maxZ - 1) : Rand.RangeInclusive(hull.minX + 1, hull.maxX - 1);
                    for (int k = horizontal ? hull.minZ + 1 : hull.minX + 1; k <= (horizontal ? hull.maxZ - 1 : hull.maxX - 1); k++)
                    {
                        if (k == gap)
                        {
                            continue;
                        }
                        PlaceWall(map, horizontal ? new IntVec3(cut, 0, k) : new IntVec3(k, 0, cut), stuff);
                    }
                }
                prev = cut + 1;
            }

            // 殘缺的屋頂
            foreach (IntVec3 c in hull.ContractedBy(1))
            {
                if (Rand.Chance(roofChance))
                {
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
            }

            // 戰利品
            foreach (CellRect room in rooms)
            {
                foreach (ThingDefCountRangeClass entry in loot)
                {
                    if (entry.thingDef == null || !Rand.Chance(0.6f))
                    {
                        continue;
                    }
                    int n = entry.countRange.RandomInRange;
                    if (n <= 0)
                    {
                        continue;
                    }
                    Thing t = ThingMaker.MakeThing(entry.thingDef, GenStuff.DefaultStuffFor(entry.thingDef));
                    t.stackCount = Mathf.Min(n, entry.thingDef.stackLimit);
                    IntVec3 at = room.Cells.Where(c => c.Standable(map) && c.GetFirstItem(map) == null).RandomElementWithFallback(room.CenterCell);
                    GenPlace.TryPlaceThing(t, at, map, ThingPlaceMode.Near);
                }
            }

            // 海洋基因包（基因設計稿 §7，只在 Biotech 啟用時）
            Thing genepack = MarineXenotypes.TryMakeShipwreckGenepack();
            if (genepack != null && rooms.Count > 0)
            {
                CellRect room = rooms.RandomElement();
                IntVec3 at = room.Cells.Where(c => c.Standable(map) && c.GetFirstItem(map) == null).RandomElementWithFallback(room.CenterCell);
                GenPlace.TryPlaceThing(genepack, at, map, ThingPlaceMode.Near);
            }

            // 四周的鋼渣
            int slag = slagChunks.RandomInRange;
            for (int i = 0; i < slag; i++)
            {
                IntVec3 c = hull.ExpandedBy(6).ClipInsideMap(map).RandomCell;
                if (!hull.Contains(c) && c.Standable(map) && c.GetFirstItem(map) == null)
                {
                    GenSpawn.Spawn(ThingDefOf.ChunkSlagSteel, c, map);
                }
            }
        }

        private static void PlaceWall(Map map, IntVec3 c, ThingDef stuff)
        {
            if (!c.InBounds(map))
            {
                return;
            }
            Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, stuff);
            GenSpawn.Spawn(wall, c, map, WipeMode.Vanish);
            // 泡在海裡多年：外殼有損傷
            wall.HitPoints = Mathf.Max(1, Mathf.RoundToInt(wall.MaxHitPoints * Rand.Range(0.3f, 0.8f)));
        }

        private static void ClearWall(Map map, IntVec3 c)
        {
            Building edifice = c.InBounds(map) ? c.GetEdifice(map) : null;
            edifice?.Destroy();
        }

        private static TerrainDef SeabedTerrain(Map map, CellRect around)
        {
            return around.ExpandedBy(8).ClipInsideMap(map).Cells
                .Select(c => map.terrainGrid.TerrainAt(c))
                .Where(t => t != null && t != DepthRimDefOf.DepthRim_Abyss && !t.passability.Equals(Traversability.Impassable))
                .GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        }

        /// <summary>地圖中央附近、深淵與岩體最少的位置。</summary>
        private static CellRect FindSpot(Map map, int w, int h)
        {
            CellRect best = CellRect.CenteredOn(map.Center, w / 2, h / 2);
            int bestScore = int.MaxValue;
            for (int i = 0; i < 60; i++)
            {
                IntVec3 c = map.Center + new IntVec3(Rand.RangeInclusive(-map.Size.x / 4, map.Size.x / 4), 0, Rand.RangeInclusive(-map.Size.z / 4, map.Size.z / 4));
                CellRect r = new CellRect(c.x - w / 2, c.z - h / 2, w, h);
                if (!r.ExpandedBy(4).InBounds(map))
                {
                    continue;
                }
                int score = 0;
                foreach (IntVec3 cell in r)
                {
                    if (map.terrainGrid.TerrainAt(cell) == DepthRimDefOf.DepthRim_Abyss)
                    {
                        score += 3;
                    }
                    else if (cell.GetEdifice(map) != null)
                    {
                        score += 1;
                    }
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    best = r;
                }
            }
            return best.ClipInsideMap(map);
        }
    }
}
