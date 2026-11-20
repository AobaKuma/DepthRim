using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水面航行的海路（Docs/海岸船舶與潛水鐘_機制設計.md §4.2）：只走地表圖層上的海，不經過陸地、冰棚與結冰的海
    /// （<see cref="IceConditions"/>：多年冰、這一季結了冰的一年冰）。搜尋沿用 <see cref="OceanRouting.Bfs"/>。
    /// 冰況隨季節變，快取以（起點、遊戲日）為鍵。
    /// </summary>
    public static class SurfaceRouting
    {
        private static readonly Dictionary<(PlanetTile, int), OceanRouting.Flood> cache = new Dictionary<(PlanetTile, int), OceanRouting.Flood>();

        private static readonly List<(PlanetTile, int)> order = new List<(PlanetTile, int)>();

        private const int CacheSize = 4;

        /// <summary>船能浮著經過的地表格：是海、沒有結冰。</summary>
        public static bool Navigable(PlanetTile tile)
        {
            if (!tile.Valid || tile.LayerDef != PlanetLayerDefOf.Surface)
            {
                return false;
            }
            Tile t = tile.Tile;
            if (t == null)
            {
                return false;
            }
            string biome = t.PrimaryBiome?.defName;
            // 先用便宜的判斷排掉陸地與溫暖的海；只有可能結冰的格子才算冰況（IceConditions 每次都要算季節溫度，整個世界跑一遍太慢）
            if (biome != "SeaIce" && (!t.WaterCovered || biome == "IceSheet"))
            {
                return false;
            }
            if (biome != "SeaIce" && t.temperature >= IceConditions.SeasonalIceBelow)
            {
                return true;
            }
            IceConditions.Info ice = IceConditions.At(tile);
            return !ice.land && !ice.Frozen;
        }

        private static int DayKey => Find.TickManager.TicksAbs / GenDate.TicksPerDay;

        private static OceanRouting.Flood FloodFrom(PlanetTile origin)
        {
            (PlanetTile, int) key = (origin, DayKey);
            if (cache.TryGetValue(key, out OceanRouting.Flood f))
            {
                return f;
            }
            PlanetLayer layer = origin.Layer;
            List<PlanetTile> nb = new List<PlanetTile>();
            Dictionary<int, bool> navigable = new Dictionary<int, bool>();
            f = OceanRouting.Bfs(layer.TilesCount, origin.tileId,
                (i, outList) =>
                {
                    nb.Clear();
                    Find.WorldGrid.GetTileNeighbors(new PlanetTile(i, layer), nb);
                    for (int k = 0; k < nb.Count; k++)
                    {
                        outList.Add(nb[k].tileId);
                    }
                },
                i =>
                {
                    if (!navigable.TryGetValue(i, out bool ok))
                    {
                        ok = Navigable(new PlanetTile(i, layer));
                        navigable[i] = ok;
                    }
                    return ok;
                });
            cache[key] = f;
            order.Add(key);
            if (order.Count > CacheSize)
            {
                cache.Remove(order[0]);
                order.RemoveAt(0);
            }
            return f;
        }

        public static int Distance(PlanetTile from, PlanetTile to)
        {
            if (!from.Valid || !to.Valid || from.Layer != to.Layer)
            {
                return OceanRouting.Unreachable;
            }
            return FloodFrom(from).dist[to.tileId];
        }

        /// <summary>從 <paramref name="from"/> 到 <paramref name="to"/> 的海路（含兩端）；到不了時 null。</summary>
        public static List<PlanetTile> Path(PlanetTile from, PlanetTile to)
        {
            List<int> ids = from.Valid && to.Valid && from.Layer == to.Layer ? OceanRouting.PathTo(FloodFrom(from), from.tileId, to.tileId) : null;
            if (ids == null)
            {
                return null;
            }
            List<PlanetTile> path = new List<PlanetTile>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                path.Add(new PlanetTile(ids[i], from.Layer));
            }
            return path;
        }

        public static void ClearCache()
        {
            cache.Clear();
            order.Clear();
        }
    }

    /// <summary>水面航行的數值（純函式，可單元測試）。</summary>
    public static class SurfaceVoyageMath
    {
        /// <summary>推力足夠時每格的航行時間（小時）：比潛艇（DMSE 的 4 小時）慢。</summary>
        public const float BaseHoursPerTile = 6f;

        /// <summary>每 1 點水面推力全速推得動的船體格數；船更大就照比例變慢。</summary>
        public const float CellsPerThrust = 50f;

        /// <summary>每格的航行時間（小時）：推力不夠推這麼大的船時照比例拉長；沒有推力回傳無限大。</summary>
        public static float HoursPerTile(float thrust, int hullCells)
        {
            if (thrust <= 0f)
            {
                return float.PositiveInfinity;
            }
            float capacity = thrust * CellsPerThrust;
            return BaseHoursPerTile * Mathf.Max(1f, hullCells / capacity);
        }

        /// <summary>一格要走多少 ticks。</summary>
        public static int TicksPerTile(float hoursPerTile)
        {
            if (float.IsInfinity(hoursPerTile) || hoursPerTile <= 0f)
            {
                return int.MaxValue;
            }
            return Mathf.Max(1, Mathf.RoundToInt(hoursPerTile * GenDate.TicksPerHour));
        }

        /// <summary>電池 <paramref name="storedWd"/> Wd 能讓 <paramref name="watts"/> W 的推進器走幾格（沒有耗電時無限）。</summary>
        public static int RangeTiles(float storedWd, float watts, float hoursPerTile)
        {
            return VoyageMath.RangeTiles(storedWd, watts, 0f, hoursPerTile);
        }

        /// <summary>
        /// 推進：目前進度 <paramref name="progress"/>（0..1，到下一格）加上 <paramref name="ticks"/> × 推力比例 ÷ 每格 ticks，
        /// 回傳走過了幾格（進度留下小數部分）。
        /// </summary>
        public static int Advance(ref float progress, int ticks, float thrustFraction, int ticksPerTile)
        {
            if (ticks <= 0 || thrustFraction <= 0f || ticksPerTile <= 0 || ticksPerTile == int.MaxValue)
            {
                return 0;
            }
            progress += ticks * Mathf.Clamp01(thrustFraction) / ticksPerTile;
            int whole = (int)progress;
            progress -= whole;
            return whole;
        }
    }
}
