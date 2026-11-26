using System;
using System.Collections.Generic;
using DMSE;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇的航線不能穿過陸地。DMSE 的轉移飛行在太空裡走直線（貪婪鄰格、距離用穿越不可通行格的格數），
    /// 海洋層上改成「只走海洋格」的最短路徑：距離、航線、目的地檢查三處用同一份廣度優先搜尋的結果。
    /// </summary>
    public static class OceanRouting
    {
        public const int Unreachable = int.MaxValue / 4;

        /// <summary>一份從起點出發的搜尋結果：每格的距離與上一格（以 tileId 為索引）。</summary>
        public sealed class Flood
        {
            public int[] dist;
            public int[] parent;
        }

        private static readonly Dictionary<PlanetTile, Flood> cache = new Dictionary<PlanetTile, Flood>();

        private static readonly List<PlanetTile> order = new List<PlanetTile>();

        private const int CacheSize = 4;

        public static bool IsOcean(PlanetTile tile)
        {
            return tile.Valid && tile.LayerDef == DepthRimDefOf.DepthRim_Ocean;
        }

        /// <summary>潛艇能進入的海洋格：生態區帶 <see cref="WaterMapExtension"/>（陸塊沒有）。</summary>
        public static bool Navigable(PlanetTile tile)
        {
            return tile.Valid && tile.Tile?.PrimaryBiome?.HasModExtension<WaterMapExtension>() == true;
        }

        /// <summary>
        /// 純函式的廣度優先搜尋（可單元測試）：<paramref name="count"/> 個節點、從 <paramref name="start"/> 出發，
        /// 只經過 <paramref name="passable"/> 的節點。起點本身不檢查。
        /// </summary>
        public static Flood Bfs(int count, int start, Action<int, List<int>> neighbors, Func<int, bool> passable)
        {
            Flood f = new Flood { dist = new int[count], parent = new int[count] };
            for (int i = 0; i < count; i++)
            {
                f.dist[i] = Unreachable;
                f.parent[i] = -1;
            }
            Queue<int> queue = new Queue<int>();
            List<int> buf = new List<int>();
            f.dist[start] = 0;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                buf.Clear();
                neighbors(cur, buf);
                for (int i = 0; i < buf.Count; i++)
                {
                    int n = buf[i];
                    if (n < 0 || n >= count || f.dist[n] != Unreachable || !passable(n))
                    {
                        continue;
                    }
                    f.dist[n] = f.dist[cur] + 1;
                    f.parent[n] = cur;
                    queue.Enqueue(n);
                }
            }
            return f;
        }

        /// <summary>從起點節點走回目標的路徑（含兩端）；到不了時 null。</summary>
        public static List<int> PathTo(Flood f, int start, int target)
        {
            if (target < 0 || target >= f.dist.Length || f.dist[target] == Unreachable)
            {
                return null;
            }
            List<int> path = new List<int>();
            for (int cur = target; cur != -1; cur = cur == start ? -1 : f.parent[cur])
            {
                path.Add(cur);
            }
            path.Reverse();
            return path;
        }

        private static Flood FloodFrom(PlanetTile origin)
        {
            if (cache.TryGetValue(origin, out Flood f))
            {
                return f;
            }
            PlanetLayer layer = origin.Layer;
            List<PlanetTile> nb = new List<PlanetTile>();
            f = Bfs(layer.TilesCount, origin.tileId,
                (i, outList) =>
                {
                    nb.Clear();
                    Find.WorldGrid.GetTileNeighbors(new PlanetTile(i, layer), nb);
                    for (int k = 0; k < nb.Count; k++)
                    {
                        outList.Add(nb[k].tileId);
                    }
                },
                i => Navigable(new PlanetTile(i, layer)));
            cache[origin] = f;
            order.Add(origin);
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
                return Unreachable;
            }
            return FloodFrom(from).dist[to.tileId];
        }

        public static List<PlanetTile> Path(PlanetTile from, PlanetTile to)
        {
            List<int> ids = from.Valid && to.Valid && from.Layer == to.Layer ? PathTo(FloodFrom(from), from.tileId, to.tileId) : null;
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

    /// <summary>
    /// 海洋層的轉移航線（DMSE 的 <see cref="TransferRouteWorker"/>，掛在海洋層的 <c>TransferEnvironmentExtension</c>）：
    /// 距離是只走海洋格的格數（到不了時是一個很大的數，超過任何航程）、航線繞過陸地、目的地不能是陸地也要有海路到得了。
    /// </summary>
    public class OceanRouteWorker : TransferRouteWorker
    {
        public override int Distance(PlanetTile from, PlanetTile to)
        {
            return OceanRouting.Distance(from, to);
        }

        /// <summary>到不了時回傳 null（目的地檢查應該已經擋下），退回 DMSE 的直線，至少不會卡住。</summary>
        public override List<PlanetTile> BuildPath(PlanetTile from, PlanetTile to)
        {
            return OceanRouting.Path(from, to);
        }

        public override string DestinationBlockReason(PlanetTile origin, PlanetTile tile)
        {
            if (!OceanRouting.Navigable(tile))
            {
                return "DepthRim.Route.Land".Translate();
            }
            if (OceanRouting.Distance(origin, tile) >= OceanRouting.Unreachable)
            {
                return "DepthRim.Route.Unreachable".Translate();
            }
            return null;
        }
    }
}
