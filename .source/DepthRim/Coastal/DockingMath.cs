using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 靠岸（M3，Docs/海岸船舶與潛水鐘_機制設計.md §5）的純計算：旋轉船體格、建築旋轉後的位置、進場路線。全部可單元測試。
    /// <para>座標：x 往東、z 往北；「轉一次」= 順時針 90°（北 → 東）。</para>
    /// </summary>
    public static class DockingMath
    {
        // ── 旋轉 ──

        /// <summary>以 <paramref name="pivot"/> 為中心把格子順時針轉 <paramref name="turns"/> 次 90°。</summary>
        public static IntVec3 RotateCell(IntVec3 c, IntVec3 pivot, int turns)
        {
            int dx = c.x - pivot.x;
            int dz = c.z - pivot.z;
            switch (((turns % 4) + 4) % 4)
            {
                case 1: return new IntVec3(pivot.x + dz, 0, pivot.z - dx);
                case 2: return new IntVec3(pivot.x - dx, 0, pivot.z - dz);
                case 3: return new IntVec3(pivot.x - dz, 0, pivot.z + dx);
                default: return c;
            }
        }

        public static Rot4 RotateRot(Rot4 rot, int turns)
        {
            return new Rot4((rot.AsInt + ((turns % 4) + 4) % 4) % 4);
        }

        /// <summary>一組格子轉完後的位置（順序對應）。</summary>
        public static List<IntVec3> RotateCells(IEnumerable<IntVec3> cells, IntVec3 pivot, int turns)
        {
            List<IntVec3> list = new List<IntVec3>();
            foreach (IntVec3 c in cells)
            {
                list.Add(RotateCell(c, pivot, turns));
            }
            return list;
        }

        /// <summary>船體的旋轉中心：外框的中心格（設計器的錨點與實際旋轉用同一個）。</summary>
        public static IntVec3 Pivot(IEnumerable<IntVec3> cells) => CellRect.FromCellList(cells).CenterCell;

        /// <summary>
        /// 多格東西旋轉後的新位置：原本佔的矩形整塊轉過去，再找出新朝向下佔同一塊矩形的位置。
        /// 非方形而且不能轉（<paramref name="rotatable"/> = false）的東西轉奇數次時沒有解（回傳 false），180° 則照樣（形狀不變）。
        /// </summary>
        public static bool RotatedPlacement(IntVec3 pos, Rot4 rot, IntVec2 size, bool rotatable, IntVec3 pivot, int turns, out IntVec3 newPos, out Rot4 newRot)
        {
            turns = ((turns % 4) + 4) % 4;
            newPos = pos;
            newRot = rot;
            if (turns == 0)
            {
                return true;
            }
            bool square = size.x == size.z;
            if (!rotatable && !square && turns % 2 == 1)
            {
                return false;
            }
            newRot = rotatable ? RotateRot(rot, turns) : rot;
            CellRect before = GenAdj.OccupiedRect(pos, rot, size);
            IntVec3 a = RotateCell(new IntVec3(before.minX, 0, before.minZ), pivot, turns);
            IntVec3 b = RotateCell(new IntVec3(before.maxX, 0, before.maxZ), pivot, turns);
            CellRect target = CellRect.FromLimits(Mathf.Min(a.x, b.x), Mathf.Min(a.z, b.z), Mathf.Max(a.x, b.x), Mathf.Max(a.z, b.z));
            IntVec3 guess = RotateCell(pos, pivot, turns);
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    IntVec3 p = new IntVec3(guess.x + dx, 0, guess.z + dz);
                    if (GenAdj.OccupiedRect(p, newRot, size).Equals(target))
                    {
                        newPos = p;
                        return true;
                    }
                }
            }
            return false;
        }

        // ── 進場路線 ──

        public const int Unreachable = int.MaxValue / 4;

        /// <summary>
        /// 每格離最近的障礙（不是水的格子）幾格（切比雪夫距離，8 鄰格）；障礙本身 0。地圖邊緣不算障礙（海延伸到地圖外）。
        /// 沒有任何障礙時是 <see cref="Unreachable"/>。
        /// </summary>
        public static int[] Clearance(int width, int height, Func<int, int, bool> water)
        {
            int[] dist = new int[width * height];
            Queue<int> queue = new Queue<int>();
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = z * width + x;
                    if (water(x, z))
                    {
                        dist[i] = Unreachable;
                    }
                    else
                    {
                        dist[i] = 0;
                        queue.Enqueue(i);
                    }
                }
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % width;
                int z = i / width;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int nz = z + dz;
                        if ((dx == 0 && dz == 0) || nx < 0 || nz < 0 || nx >= width || nz >= height)
                        {
                            continue;
                        }
                        int n = nz * width + nx;
                        if (dist[n] > dist[i] + 1)
                        {
                            dist[n] = dist[i] + 1;
                            queue.Enqueue(n);
                        }
                    }
                }
            }
            return dist;
        }

        /// <summary>船沿長邊前進時要多少淨空（格）：半個船寬（取短邊）。</summary>
        public static int PassClearance(int w, int h) => Mathf.CeilToInt(Mathf.Min(w, h) / 2f);

        /// <summary>船原地迴轉要多少淨空（格）：半條對角線。</summary>
        public static int TurnClearance(int w, int h) => Mathf.CeilToInt(Mathf.Sqrt(w * w + h * h) / 2f);

        /// <summary>找迴轉點時，離泊位最近的那一圈再往外看幾格（在這範圍內挑離進場口最近的）。</summary>
        public const int TurnSearchSlack = 3;

        public enum PlanFailure
        {
            None,
            NoTurningRoom,
            NoRoute,
        }

        /// <summary>進場路線：進場口（地圖邊上）→ 迴轉點 → 泊位中心。</summary>
        public sealed class Approach
        {
            public PlanFailure failure;
            public IntVec3 entry;
            public IntVec3 turn;
            public IntVec3 berth;
            /// <summary>進場口到迴轉點的格子（含兩端）。</summary>
            public List<IntVec3> path = new List<IntVec3>();
            /// <summary>只留轉彎處的折線：進場口、各轉折、迴轉點、泊位中心（預覽與演出用）。</summary>
            public List<IntVec3> corners = new List<IntVec3>();
            /// <summary>進場時的船頭方向（路線第一段的方向，地圖單位向量）。</summary>
            public Vector2 initialHeading;

            public bool Ok => failure == PlanFailure.None;
        }

        /// <summary>
        /// 給定水域（<paramref name="water"/>：船能浮的格子，泊位本身也算）、泊位格（船體轉好、放好後佔的格子）與泊位外框尺寸，算進場路線（§5.3）：
        /// <list type="number">
        /// <item>迴轉點：從泊位格往外、只走水的廣度優先，第一個淨空 ≥ 半對角線的格子（船在這裡轉到玩家選的朝向）。</item>
        /// <item>路線：從所有淨空 ≥ 半船寬的地圖邊緣格同時出發的廣度優先，走淨空 ≥ 半船寬的格子到迴轉點；最短的那條（進場口就是起點）。</item>
        /// <item>初始船頭：路線第一段的方向。</item>
        /// </list>
        /// 同長的進場口取第一個找到的（設計稿的「最避風」先不做）。
        /// </summary>
        public static Approach Plan(int width, int height, Func<int, int, bool> water, ICollection<IntVec3> berthCells, int berthW, int berthH)
        {
            Approach result = new Approach();
            if (berthCells.Count == 0)
            {
                result.failure = PlanFailure.NoTurningRoom;
                return result;
            }
            HashSet<IntVec3> berth = berthCells as HashSet<IntVec3> ?? new HashSet<IntVec3>(berthCells);
            bool Water(int x, int z) => x >= 0 && z >= 0 && x < width && z < height && (water(x, z) || berth.Contains(new IntVec3(x, 0, z)));
            int[] clear = Clearance(width, height, Water);
            int turnNeed = TurnClearance(berthW, berthH);
            int passNeed = PassClearance(berthW, berthH);
            result.berth = CellRect.FromCellList(berthCells).CenterCell;

            // 1. 從地圖邊緣出發的廣度優先（4 鄰格；淨空不足半船寬的格子不能走）：每格離進場口多遠、從哪一格來
            int[] parent = new int[width * height];
            int[] edgeDist = new int[width * height];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = -2;
                edgeDist[i] = Unreachable;
            }
            Queue<int> q = new Queue<int>();
            for (int x = 0; x < width; x++)
            {
                TrySeed(x, 0);
                TrySeed(x, height - 1);
            }
            for (int z = 1; z < height - 1; z++)
            {
                TrySeed(0, z);
                TrySeed(width - 1, z);
            }
            void TrySeed(int x, int z)
            {
                int i = z * width + x;
                if (parent[i] == -2 && clear[i] >= passNeed)
                {
                    parent[i] = -1;
                    edgeDist[i] = 0;
                    q.Enqueue(i);
                }
            }
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int x = i % width;
                int z = i / width;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int nz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= width || nz >= height)
                    {
                        continue;
                    }
                    int n = nz * width + nx;
                    if (parent[n] != -2 || clear[n] < passNeed)
                    {
                        continue;
                    }
                    parent[n] = i;
                    edgeDist[n] = edgeDist[i] + 1;
                    q.Enqueue(n);
                }
            }

            // 2. 迴轉點：從泊位往外（只走水）找淨空 ≥ 半對角線的格子；離泊位最近的那一圈再往外 TurnSearchSlack 格內，
            // 取離進場口最近的（船在靠外海那一側轉向，不會先開過泊位再倒回來）
            int[] berthDist = new int[width * height];
            for (int i = 0; i < berthDist.Length; i++)
            {
                berthDist[i] = -1;
            }
            foreach (IntVec3 c in berth)
            {
                if (c.x < 0 || c.z < 0 || c.x >= width || c.z >= height)
                {
                    continue;
                }
                int i = c.z * width + c.x;
                if (berthDist[i] < 0)
                {
                    berthDist[i] = 0;
                    q.Enqueue(i);
                }
            }
            int firstLevel = -1;
            int turnIndex = -1;
            bool anyRoom = false;
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                if (firstLevel >= 0 && berthDist[i] > firstLevel + TurnSearchSlack)
                {
                    break;
                }
                if (clear[i] >= turnNeed)
                {
                    anyRoom = true;
                    if (firstLevel < 0)
                    {
                        firstLevel = berthDist[i];
                    }
                    if (parent[i] != -2 && (turnIndex < 0 || edgeDist[i] < edgeDist[turnIndex]))
                    {
                        turnIndex = i;
                    }
                }
                int x = i % width;
                int z = i / width;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int nz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (!Water(nx, nz))
                    {
                        continue;
                    }
                    int n = nz * width + nx;
                    if (berthDist[n] >= 0)
                    {
                        continue;
                    }
                    berthDist[n] = berthDist[i] + 1;
                    q.Enqueue(n);
                }
            }
            if (!anyRoom)
            {
                result.failure = PlanFailure.NoTurningRoom;
                return result;
            }
            if (turnIndex < 0)
            {
                result.failure = PlanFailure.NoRoute;
                return result;
            }
            result.turn = new IntVec3(turnIndex % width, 0, turnIndex / width);
            for (int cur = turnIndex; cur != -1; cur = parent[cur])
            {
                result.path.Add(new IntVec3(cur % width, 0, cur / width));
            }
            result.path.Reverse();
            result.entry = result.path[0];
            result.corners = Corners(result.path);
            if (result.corners.Count == 0 || result.corners[result.corners.Count - 1] != result.berth)
            {
                result.corners.Add(result.berth);
            }
            result.initialHeading = InitialHeading(result.path, result.berth);
            return result;
        }

        /// <summary>路線只留轉彎處（含兩端）。</summary>
        public static List<IntVec3> Corners(List<IntVec3> path)
        {
            List<IntVec3> corners = new List<IntVec3>();
            if (path.Count == 0)
            {
                return corners;
            }
            corners.Add(path[0]);
            for (int i = 1; i < path.Count - 1; i++)
            {
                IntVec3 a = path[i] - path[i - 1];
                IntVec3 b = path[i + 1] - path[i];
                if (a != b)
                {
                    corners.Add(path[i]);
                }
            }
            if (path.Count > 1)
            {
                corners.Add(path[path.Count - 1]);
            }
            return corners;
        }

        /// <summary>船頭朝路線第一段（路線只有一格時朝泊位）。</summary>
        public static Vector2 InitialHeading(List<IntVec3> path, IntVec3 berth)
        {
            IntVec3 from = path.Count > 0 ? path[0] : berth;
            IntVec3 to = path.Count > 1 ? path[1] : berth;
            Vector2 d = new Vector2(to.x - from.x, to.z - from.z);
            return d.sqrMagnitude > 0f ? d.normalized : Vector2.up;
        }
    }
}
