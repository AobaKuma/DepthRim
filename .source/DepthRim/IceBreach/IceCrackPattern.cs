using System;
using System.Collections.Generic;

namespace DepthRim
{
    /// <summary>冰面一格在破冰後的狀態（設計稿 §5.1）。</summary>
    public enum IceCell : byte
    {
        Intact,
        Crack,
        Rubble,
        OpenWater,
        Hull
    }

    /// <summary>
    /// 冰層開裂的圖樣（設計稿 §5.2，2026-11-13 加大）：純函式，不依賴 Map，相同輸入一定得到相同圖樣。
    /// 以船體佔地的距離場為基準（船體是長的，不是一個點）：
    /// <list type="number">
    /// <item>距船體 2 格內是開放水（船體浮在破口裡）。</item>
    /// <item>再往外到 d_r = max(3, round(0.6 × l_c × √R)) 是碎冰，邊緣用雜訊打散 ±1.5 格；碎冰帶裡夾著幾塊開放水（碎冰之間的水縫）。</item>
    /// <item>楔形碎塊：船體四周分成 2 n_r 個扇區，約三成的扇區從碎冰帶往外再翻起 0.6 l_c 的冰板（整塊變碎冰）。</item>
    /// <item>每個接觸點（破冰結構）發出 n_r = clamp(6 + round(3(R − 1)), 6, 12) 條放射裂紋，長度 l_c × (3 + 1.2(R − 1)) × 0.8～1.2，
    /// 前半段兩格寬；每格方向抖動 ±12°，每 3 格 20% 分岔（剩餘長度的一半，最多兩層）。</item>
    /// <item>距船體 r_c = d_r + 0.9 l_c 附近（±0.6 格）、雜訊 &gt; 0.3 的格子是環狀裂紋；更外面 r_c2 = d_r + 1.8 l_c 還有一圈較稀疏的。</item>
    /// </list>
    /// 部分開裂（R &lt; 1）只有放射裂紋，長度 × 0.6，沒有開放水、碎冰與環。
    /// 每格另外給「出現時間」（秒，出水演出用）：放射裂紋沿裂紋從接觸點往外以 <see cref="CrackSpeed"/> 格／秒長出，
    /// 環、碎冰與開放水在 <see cref="BreakTime"/> 一起出現，外環稍晚。
    /// </summary>
    public static class IceCrackPattern
    {
        /// <summary>放射裂紋的畫面速度（格／秒，不是物理值）。</summary>
        public const float CrackSpeed = 24f;

        /// <summary>裂紋開始長出的時刻（演出第幾秒）。</summary>
        public const float CrackStart = 0.4f;

        /// <summary>冰面破裂的時刻（演出第幾秒）：環、碎冰、開放水一起出現。</summary>
        public const float BreakTime = 3.5f;

        public struct Result
        {
            public int width;
            public int height;
            public IceCell[] cells;
            public float[] reveal;
            public int radialCount;
            public int rubbleRadius;
            public float ringRadius;
            public float[] distance;

            public IceCell At(int x, int z) => cells[z * width + x];

            public float RevealAt(int x, int z) => reveal[z * width + x];
        }

        public static int RadialCount(float ratio)
        {
            int n = 6 + (int)Math.Round(3f * (ratio - 1f));
            return n < 6 ? 6 : (n > 12 ? 12 : n);
        }

        public static int RubbleRadius(float lCells, float ratio)
        {
            int d = (int)Math.Round(0.6f * lCells * Math.Sqrt(Math.Max(0f, ratio)));
            return Math.Max(3, d);
        }

        public static float RingRadius(float lCells, float ratio)
        {
            return RubbleRadius(lCells, ratio) + 0.9f * lCells;
        }

        public static float OuterRingRadius(float lCells, float ratio)
        {
            return RubbleRadius(lCells, ratio) + 1.8f * lCells;
        }

        public static float RadialLength(float lCells, float ratio, bool partial)
        {
            float l = Math.Max(8f, lCells * (3f + 1.2f * (ratio - 1f)));
            return partial ? l * 0.6f : l;
        }

        /// <param name="hull">船體格（地圖座標）。</param>
        /// <param name="contacts">接觸點（破冰結構中心；沒有時用船體中心）。</param>
        /// <param name="lCells">冰的特徵長度（格）。</param>
        public static Result Generate(int width, int height, IList<(int x, int z)> hull, IList<(int x, int z)> contacts, float lCells, float ratio, int seed)
        {
            Result r = new Result { width = width, height = height, cells = new IceCell[width * height], reveal = new float[width * height] };
            bool partial = ratio < 1f;
            float[] dist = DistanceField(width, height, hull);
            r.distance = dist;
            foreach ((int x, int z) in hull)
            {
                if (In(width, height, x, z))
                {
                    r.cells[z * width + x] = IceCell.Hull;
                }
            }
            int rubble = RubbleRadius(lCells, ratio);
            float ring = RingRadius(lCells, ratio);
            float outer = OuterRingRadius(lCells, ratio);
            int n = RadialCount(ratio);
            r.radialCount = n;
            r.rubbleRadius = partial ? 0 : rubble;
            r.ringRadius = partial ? 0f : ring;
            (int x, int z) center = Center(hull);
            if (!partial)
            {
                int sectors = n * 2;
                float wedge = rubble + 0.6f * lCells;
                for (int i = 0; i < r.cells.Length; i++)
                {
                    if (r.cells[i] == IceCell.Hull)
                    {
                        continue;
                    }
                    float d = dist[i];
                    int x = i % width;
                    int z = i / width;
                    double angle = Math.Atan2(z - center.z, x - center.x);
                    int sector = (int)Math.Floor((angle + Math.PI) / (2 * Math.PI) * sectors) % sectors;
                    if (d <= 2.5f)
                    {
                        r.cells[i] = IceCell.OpenWater;
                    }
                    else if (d <= rubble + (Noise(x, z, seed) * 3f - 1.5f))
                    {
                        // 碎冰帶：夾著幾塊碎冰之間的水縫
                        r.cells[i] = Noise(x, z, seed ^ 0x77A7) > 0.88f ? IceCell.OpenWater : IceCell.Rubble;
                    }
                    else if (d <= wedge && Noise(sector, 0, seed ^ 0x3ED6E) < 0.3f)
                    {
                        // 楔形碎塊：這個扇區的冰板整塊被頂翻
                        r.cells[i] = IceCell.Rubble;
                    }
                    else if (Math.Abs(d - ring) <= 0.6f && Noise(x, z, seed ^ 0x51ED) > 0.3f)
                    {
                        r.cells[i] = IceCell.Crack;
                    }
                    else if (Math.Abs(d - outer) <= 0.5f && Noise(x, z, seed ^ 0x0E7) > 0.55f)
                    {
                        r.cells[i] = IceCell.Crack;
                        r.reveal[i] = BreakTime + 0.6f;
                        continue;
                    }
                    else
                    {
                        continue;
                    }
                    r.reveal[i] = BreakTime;
                }
            }

            Random rand = new Random(seed);
            float baseLength = RadialLength(lCells, ratio, partial);
            IList<(int x, int z)> from = contacts != null && contacts.Count > 0 ? contacts : new[] { center };
            foreach ((int cx, int cz) in from)
            {
                double start = rand.NextDouble() * Math.PI * 2;
                for (int k = 0; k < n; k++)
                {
                    double angle = start + k * Math.PI * 2 / n + (rand.NextDouble() * 2 - 1) * (15 * Math.PI / 180);
                    float length = baseLength * (0.8f + 0.4f * (float)rand.NextDouble());
                    Walk(r, cx + 0.5, cz + 0.5, angle, length, 0, 0, rand);
                }
            }
            return r;
        }

        /// <summary>
        /// 沿著一條裂紋往外走：在船體、開放水與碎冰帶裡不算長度，走出去後每格標成裂紋，前半段（主裂紋）兩格寬。
        /// <paramref name="steps"/> 是從接觸點起算走過的格數（決定出現時間）。
        /// </summary>
        private static void Walk(Result r, double x, double z, double angle, float length, int depth, int steps, Random rand)
        {
            float walked = 0f;
            int sinceBranch = 0;
            for (int guard = 0; guard < 4000 && walked < length; guard++)
            {
                angle += (rand.NextDouble() * 2 - 1) * (12 * Math.PI / 180);
                x += Math.Cos(angle);
                z += Math.Sin(angle);
                steps++;
                int ix = (int)Math.Floor(x);
                int iz = (int)Math.Floor(z);
                if (!In(r.width, r.height, ix, iz))
                {
                    return;
                }
                IceCell c = r.cells[iz * r.width + ix];
                if (c == IceCell.Hull || c == IceCell.OpenWater || c == IceCell.Rubble)
                {
                    continue;
                }
                float time = CrackStart + steps / CrackSpeed;
                Mark(r, ix, iz, time);
                if (depth == 0 && walked < length * 0.5f)
                {
                    // 主裂紋前半段加寬：往法線方向多標一格
                    int wx = (int)Math.Floor(x - Math.Sin(angle));
                    int wz = (int)Math.Floor(z + Math.Cos(angle));
                    Mark(r, wx, wz, time);
                }
                walked += 1f;
                if (++sinceBranch >= 3)
                {
                    sinceBranch = 0;
                    if (depth < 2 && rand.NextDouble() < 0.20)
                    {
                        double side = rand.NextDouble() < 0.5 ? -1 : 1;
                        Walk(r, x, z, angle + side * (25 + rand.NextDouble() * 20) * Math.PI / 180, (length - walked) * 0.5f, depth + 1, steps, rand);
                    }
                }
            }
        }

        /// <summary>標成裂紋：只標完整的冰，已經是裂紋時保留比較早的出現時間。</summary>
        private static void Mark(Result r, int x, int z, float time)
        {
            if (!In(r.width, r.height, x, z))
            {
                return;
            }
            int i = z * r.width + x;
            IceCell c = r.cells[i];
            if (c == IceCell.Intact)
            {
                r.cells[i] = IceCell.Crack;
                r.reveal[i] = time;
            }
            else if (c == IceCell.Crack && time < r.reveal[i])
            {
                r.reveal[i] = time;
            }
        }

        /// <summary>到船體的距離（八鄰接倒角距離：直走 1、斜走 √2）。船體格是 0；沒有船體時全部無限大。</summary>
        public static float[] DistanceField(int width, int height, IList<(int x, int z)> hull)
        {
            const float diag = 1.41421356f;
            float[] d = new float[width * height];
            for (int i = 0; i < d.Length; i++)
            {
                d[i] = float.PositiveInfinity;
            }
            foreach ((int x, int z) in hull)
            {
                if (In(width, height, x, z))
                {
                    d[z * width + x] = 0f;
                }
            }
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = z * width + x;
                    float v = d[i];
                    if (x > 0) v = Math.Min(v, d[i - 1] + 1f);
                    if (z > 0)
                    {
                        v = Math.Min(v, d[i - width] + 1f);
                        if (x > 0) v = Math.Min(v, d[i - width - 1] + diag);
                        if (x < width - 1) v = Math.Min(v, d[i - width + 1] + diag);
                    }
                    d[i] = v;
                }
            }
            for (int z = height - 1; z >= 0; z--)
            {
                for (int x = width - 1; x >= 0; x--)
                {
                    int i = z * width + x;
                    float v = d[i];
                    if (x < width - 1) v = Math.Min(v, d[i + 1] + 1f);
                    if (z < height - 1)
                    {
                        v = Math.Min(v, d[i + width] + 1f);
                        if (x < width - 1) v = Math.Min(v, d[i + width + 1] + diag);
                        if (x > 0) v = Math.Min(v, d[i + width - 1] + diag);
                    }
                    d[i] = v;
                }
            }
            return d;
        }

        /// <summary>每格固定的雜訊（0～1），只看座標與種子。</summary>
        public static float Noise(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(seed * 83492791);
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        public static (int x, int z) Center(IList<(int x, int z)> hull)
        {
            if (hull == null || hull.Count == 0)
            {
                return (0, 0);
            }
            long sx = 0, sz = 0;
            foreach ((int x, int z) in hull)
            {
                sx += x;
                sz += z;
            }
            return ((int)(sx / hull.Count), (int)(sz / hull.Count));
        }

        private static bool In(int width, int height, int x, int z)
        {
            return x >= 0 && z >= 0 && x < width && z < height;
        }
    }
}
