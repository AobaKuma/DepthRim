using System;
using System.Collections.Generic;

namespace DepthRim
{
    /// <summary>
    /// 牆體兩側是哪兩個房間、中間隔了多厚（設計稿 §5.1 的「雙標籤 BFS」）。純函式，以格子索引 z × width + x 運算，可單元測試。
    ///
    /// <para>障壁格（牆、門、自然岩……）裡做多源 BFS：每個與房間正交相鄰的障壁格以那個房間為標籤、距離 1 出發，
    /// 只在障壁格裡擴散，距離上限 <see cref="MaxThickness"/>。每格最多記住兩個不同房間的標籤；
    /// 有兩個標籤的格子就是把那兩個房間隔開的牆的一部分，厚度 = 兩個距離相加減一。
    /// 只有一個標籤的格子（岩體內部、只面對一個房間的牆）不受力。</para>
    /// </summary>
    public static class WallPressureTopology
    {
        public const int MaxThickness = 4;

        public struct Entry
        {
            /// <summary>牆格。</summary>
            public int cell;

            /// <summary>兩側房間各一個代表格（與牆相鄰的那一格可通行格），用的時候再查回房間。</summary>
            public int seedA;

            public int seedB;

            public int thickness;
        }

        private const int None = int.MinValue;

        /// <param name="isBarrier">這格是不是障壁。</param>
        /// <param name="roomKey">非障壁格的房間代號（-1 = 不屬於任何房間，例如深淵）。</param>
        /// <param name="inBand">只在這些格子裡計算（例如密閉艙室附近）；null = 全圖。</param>
        public static List<Entry> Build(int width, int height, Func<int, bool> isBarrier, Func<int, int> roomKey, Func<int, bool> inBand = null)
        {
            int count = width * height;
            int[] keyA = new int[count];
            int[] keyB = new int[count];
            int[] distA = new int[count];
            int[] distB = new int[count];
            int[] seedA = new int[count];
            int[] seedB = new int[count];
            for (int i = 0; i < count; i++)
            {
                keyA[i] = None;
                keyB[i] = None;
            }
            Queue<(int cell, int key, int dist, int seed)> queue = new Queue<(int, int, int, int)>();

            bool Offer(int cell, int key, int dist, int seed)
            {
                if (keyA[cell] == key || keyB[cell] == key)
                {
                    return false;
                }
                if (keyA[cell] == None)
                {
                    keyA[cell] = key;
                    distA[cell] = dist;
                    seedA[cell] = seed;
                }
                else if (keyB[cell] == None)
                {
                    keyB[cell] = key;
                    distB[cell] = dist;
                    seedB[cell] = seed;
                }
                else
                {
                    return false;
                }
                queue.Enqueue((cell, key, dist, seed));
                return true;
            }

            // 種子：與房間正交相鄰的障壁格，距離 1（全部先入列，BFS 依距離由近到遠處理，先到的標籤就是最近的）
            for (int cell = 0; cell < count; cell++)
            {
                if (!isBarrier(cell) || (inBand != null && !inBand(cell)))
                {
                    continue;
                }
                int x = cell % width;
                int z = cell / width;
                ForCardinal(x, z, width, height, n =>
                {
                    if (isBarrier(n))
                    {
                        return;
                    }
                    int key = roomKey(n);
                    if (key >= 0)
                    {
                        Offer(cell, key, 1, n);
                    }
                });
            }

            while (queue.Count > 0)
            {
                (int cell, int key, int dist, int seed) = queue.Dequeue();
                if (dist >= MaxThickness)
                {
                    continue;
                }
                int x = cell % width;
                int z = cell / width;
                ForCardinal(x, z, width, height, n =>
                {
                    if (isBarrier(n) && (inBand == null || inBand(n)))
                    {
                        Offer(n, key, dist + 1, seed);
                    }
                });
            }

            List<Entry> result = new List<Entry>();
            for (int cell = 0; cell < count; cell++)
            {
                if (keyB[cell] == None)
                {
                    continue;
                }
                int thickness = distA[cell] + distB[cell] - 1;
                if (thickness <= MaxThickness)
                {
                    result.Add(new Entry { cell = cell, seedA = seedA[cell], seedB = seedB[cell], thickness = thickness });
                }
            }
            return result;
        }

        private static void ForCardinal(int x, int z, int width, int height, Action<int> action)
        {
            if (z + 1 < height) { action((z + 1) * width + x); }
            if (x + 1 < width) { action(z * width + x + 1); }
            if (z - 1 >= 0) { action((z - 1) * width + x); }
            if (x - 1 >= 0) { action(z * width + x - 1); }
        }

        /// <summary>
        /// 一格牆承受的負載：兩側壓力差 × 層級水壓 ÷ 厚度（最多算兩格：疊兩層牆有加成，再疊下去沒有）。
        /// <paramref name="pA"/>／<paramref name="pB"/> 是兩側房間的壓力比例（0 = 一大氣壓，1 = 外部水壓）。
        /// </summary>
        public static float Load(float pA, float pB, DepthTier tier, int thickness)
        {
            float dp = pA > pB ? pA - pB : pB - pA;
            int t = thickness < 1 ? 1 : thickness > 2 ? 2 : thickness;
            return dp * FloodingMath.PressureFactor(tier) / t;
        }

        /// <summary>牆能承受的負載：依它的耐壓等級（淺海 1、變溫層 2、深海 4、海床 8）。</summary>
        public static float Capacity(DepthTier rating)
        {
            return FloodingMath.PressureFactor(rating);
        }

        /// <summary>每次更新（250 ticks）每 1 點超載造成的損傷。一般牆（300 HP）在深海約 1 天壓壞、在變溫層約 3 天。</summary>
        public const float DamagePerExcess = 0.4f;

        public static float DamagePerUpdate(float load, float capacity)
        {
            float excess = load - capacity;
            return excess > 0f ? excess * DamagePerExcess : 0f;
        }
    }
}
