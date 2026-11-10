using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace DepthRim
{
    /// <summary>
    /// 在地表格網上算出海底地形的骨架（世界生成用，一次算完）：
    /// <list type="bullet">
    /// <item><b>離岸距離</b>與<b>最近的海岸格</b>：從所有陸地（含冰原）多源廣度優先。</item>
    /// <item><b>中洋脊軸線</b>：洋盆的中線（中軸）。相鄰兩個水格的「最近海岸」分別在洋盆兩側（相隔遠大於離岸距離）時，
    /// 這兩格之間就是中線。大西洋中洋脊正是這樣落在兩岸正中間。太窄的海（離岸不到 <see cref="RidgeMinCoastDistance"/> 格）沒有中洋脊。</item>
    /// <item><b>離脊距離</b>：從軸線廣度優先；決定洋盆深度（離脊越遠越深）。</item>
    /// <item><b>活動大陸邊緣</b>：每段海岸依低頻雜訊決定是不是隱沒帶，水格沿用最近海岸的結果，所以整段海岸外都有連續的海溝。</item>
    /// </list>
    /// </summary>
    public class SeafloorGeometry
    {
        /// <summary>水格離岸要多遠，才可能是中洋脊軸線。</summary>
        public const int RidgeMinCoastDistance = 7;

        /// <summary>兩側海岸至少要相隔幾倍的離岸距離，才算洋盆的兩側（排除同一段海岸上的相鄰點）。</summary>
        public const float RidgeSeparationFactor = 1.3f;

        /// <summary>活動大陸邊緣的雜訊門檻（約四分之一的海岸）。</summary>
        public const float ActiveMarginThreshold = 0.6f;

        private const int MaxRidgeDistance = 60;

        /// <summary>陸地連通塊至少要佔全部陸地的這個比例（或至少 <see cref="ContinentMinTiles"/> 格），才算大陸。</summary>
        public const float ContinentMinFraction = 0.03f;

        public const int ContinentMinTiles = 400;

        /// <summary>陸地（含冰原）的連通塊中，夠大的才算大陸；只有一塊陸地時它一定是大陸。</summary>
        private static bool[] Continents(PlanetLayer surface, List<PlanetTile> neighbors)
        {
            int count = surface.TilesCount;
            int[] component = new int[count];
            List<int> sizes = new List<int>();
            int landTotal = 0;
            Queue<int> open = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                component[i] = -1;
            }
            for (int s = 0; s < count; s++)
            {
                if (component[s] >= 0 || surface[s].WaterCovered)
                {
                    continue;
                }
                int id = sizes.Count;
                int size = 0;
                component[s] = id;
                open.Enqueue(s);
                while (open.Count > 0)
                {
                    int i = open.Dequeue();
                    size++;
                    surface.GetTileNeighbors(new PlanetTile(i, surface), neighbors);
                    for (int k = 0; k < neighbors.Count; k++)
                    {
                        int j = neighbors[k].tileId;
                        if (component[j] < 0 && !surface[j].WaterCovered)
                        {
                            component[j] = id;
                            open.Enqueue(j);
                        }
                    }
                }
                sizes.Add(size);
                landTotal += size;
            }
            int largest = sizes.Count > 0 ? sizes.Max() : 0;
            int min = Mathf.Min(Mathf.Max(ContinentMinTiles, (int)(landTotal * ContinentMinFraction)), largest);
            bool[] result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = component[i] >= 0 && sizes[component[i]] >= min;
            }
            return result;
        }

        public readonly int[] coastDistance;

        public readonly int[] nearestCoast;

        public readonly bool[] ridgeAxis;

        public readonly int[] ridgeDistance;

        public readonly bool[] activeMargin;

        public int RidgeAxisTiles { get; private set; }

        public int ActiveCoastTiles { get; private set; }

        public SeafloorGeometry(PlanetLayer surface, int seed)
        {
            int count = surface.TilesCount;
            coastDistance = new int[count];
            nearestCoast = new int[count];
            ridgeAxis = new bool[count];
            ridgeDistance = new int[count];
            activeMargin = new bool[count];
            List<PlanetTile> neighbors = new List<PlanetTile>();

            // 1. 離岸距離與最近的海岸格（陸地 0，含冰原：冰原下是大陸地殼）
            Queue<int> open = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                if (!surface[i].WaterCovered)
                {
                    coastDistance[i] = 0;
                    nearestCoast[i] = i;
                    open.Enqueue(i);
                }
                else
                {
                    coastDistance[i] = int.MaxValue;
                    nearestCoast[i] = -1;
                }
            }
            while (open.Count > 0)
            {
                int i = open.Dequeue();
                surface.GetTileNeighbors(new PlanetTile(i, surface), neighbors);
                for (int k = 0; k < neighbors.Count; k++)
                {
                    int j = neighbors[k].tileId;
                    if (coastDistance[j] > coastDistance[i] + 1)
                    {
                        coastDistance[j] = coastDistance[i] + 1;
                        nearestCoast[j] = nearestCoast[i];
                        open.Enqueue(j);
                    }
                }
            }
            for (int i = 0; i < count; i++)
            {
                if (coastDistance[i] == int.MaxValue)
                {
                    coastDistance[i] = OceanBiomeSelector.MaxCoastDistance; // 整個世界都沒有陸地
                }
            }

            // 2. 活動大陸邊緣：依海岸格的位置取低頻雜訊，水格沿用最近海岸的結果。
            //    只有大陸的海岸會是隱沒帶：小島四周不會圍出一圈海溝
            bool[] continent = Continents(surface, neighbors);
            Perlin margin = new Perlin(0.016, 2.0, 0.5, 3, seed ^ 0x4D41, QualityMode.Medium);
            bool[] coastActive = new bool[count];
            for (int i = 0; i < count; i++)
            {
                if (coastDistance[i] == 0 && continent[i])
                {
                    Vector3 p = surface.GetTileCenter(i).normalized * 100f;
                    coastActive[i] = margin.GetValue(p.x, p.y, p.z) * 0.5 + 0.5 >= ActiveMarginThreshold;
                }
            }
            for (int i = 0; i < count; i++)
            {
                int c = nearestCoast[i];
                activeMargin[i] = c >= 0 && coastActive[c];
                if (activeMargin[i] && coastDistance[i] == 1)
                {
                    ActiveCoastTiles++;
                }
            }

            // 3. 中洋脊軸線：只看大陸（小島不算），相鄰水格的最近大陸在洋盆兩側。
            //    小島也算的話，每座小島周圍都會圍出一圈脊，變成蜂巢狀；真實的中洋脊是在大陸之間的幾條長線。
            int[] continentDistance = new int[count];
            int[] nearestContinent = new int[count];
            for (int i = 0; i < count; i++)
            {
                continentDistance[i] = continent[i] ? 0 : int.MaxValue;
                nearestContinent[i] = continent[i] ? i : -1;
                if (continent[i])
                {
                    open.Enqueue(i);
                }
            }
            while (open.Count > 0)
            {
                int i = open.Dequeue();
                surface.GetTileNeighbors(new PlanetTile(i, surface), neighbors);
                for (int k = 0; k < neighbors.Count; k++)
                {
                    int j = neighbors[k].tileId;
                    if (continentDistance[j] > continentDistance[i] + 1)
                    {
                        continentDistance[j] = continentDistance[i] + 1;
                        nearestContinent[j] = nearestContinent[i];
                        open.Enqueue(j);
                    }
                }
            }
            float tileAngle = surface.AverageTileSize / Mathf.Max(surface.Radius, 1f);
            for (int i = 0; i < count; i++)
            {
                int d = continentDistance[i];
                if (coastDistance[i] < RidgeMinCoastDistance || d == int.MaxValue || nearestContinent[i] < 0)
                {
                    continue;
                }
                Vector3 ci = surface.GetTileCenter(nearestContinent[i]).normalized;
                surface.GetTileNeighbors(new PlanetTile(i, surface), neighbors);
                for (int k = 0; k < neighbors.Count; k++)
                {
                    int j = neighbors[k].tileId;
                    // 只標中線一側（離大陸較遠或相同的那格），軸線才是一格寬
                    if (nearestContinent[j] < 0 || continentDistance[j] > d)
                    {
                        continue;
                    }
                    Vector3 cj = surface.GetTileCenter(nearestContinent[j]).normalized;
                    float separation = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ci, cj), -1f, 1f)) / tileAngle;
                    if (separation >= RidgeSeparationFactor * d)
                    {
                        ridgeAxis[i] = true;
                        RidgeAxisTiles++;
                        break;
                    }
                }
            }

            // 4. 離脊距離（只在水裡走，可以越過小島）
            for (int i = 0; i < count; i++)
            {
                ridgeDistance[i] = ridgeAxis[i] ? 0 : OceanBiomeSelector.NoRidge;
                if (ridgeAxis[i])
                {
                    open.Enqueue(i);
                }
            }
            while (open.Count > 0)
            {
                int i = open.Dequeue();
                if (ridgeDistance[i] >= MaxRidgeDistance)
                {
                    continue;
                }
                surface.GetTileNeighbors(new PlanetTile(i, surface), neighbors);
                for (int k = 0; k < neighbors.Count; k++)
                {
                    int j = neighbors[k].tileId;
                    if (ridgeDistance[j] > ridgeDistance[i] + 1)
                    {
                        ridgeDistance[j] = ridgeDistance[i] + 1;
                        open.Enqueue(j);
                    }
                }
            }
        }
    }
}
