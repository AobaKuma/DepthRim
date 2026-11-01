using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace DepthRim
{
    /// <summary>
    /// 海洋層的格子：依正上方的地表格決定生態區（<see cref="OceanBiomeSelector"/>），
    /// 再依生態區給地勢（岩體與礦脈多寡，來自 <see cref="WaterMapExtension.hilliness"/>）與溫度。
    ///
    /// <para><b>找正上方的地表格：</b>原版的 <c>GetClosestTile_NewTemp</c> 每次都做一次搜尋，十幾萬格太慢；
    /// 這裡先把地表格依經緯度分桶，再到所在的桶與相鄰桶找最近的格心。地表層一定比海洋層先生成
    /// （<see cref="WorldGenerator"/> 依圖層 ID 依序生成，海洋層是後來註冊的）。</para>
    /// </summary>
    public class WorldGenStep_OceanTiles : WorldGenStep_Tiles
    {
        public override int SeedPart => 734151820;

        private const int LonBuckets = 180;

        private const int LatBuckets = 90;

        private List<int>[] buckets;

        private PlanetLayer surface;

        private Perlin ventNoise;

        private Perlin trenchNoise;

        private Perlin variationNoise;

        private readonly List<PlanetTile> tmpNeighbors = new List<PlanetTile>();

        private readonly Dictionary<OceanBiome, int> counts = new Dictionary<OceanBiome, int>();

        public override void GenerateFresh(string seed, PlanetLayer layer)
        {
            int s = GenText.StableStringHash(seed) ^ SeedPart;
            // 取樣點是半徑 100 的球面座標，頻率比照原版世界雜訊的量級（0.01–0.05）
            ventNoise = new Perlin(0.045, 2.0, 0.5, 4, s, QualityMode.Medium);
            trenchNoise = new Perlin(0.018, 2.0, 0.45, 3, s + 1, QualityMode.Medium);
            variationNoise = new Perlin(0.03, 2.0, 0.5, 4, s + 2, QualityMode.Medium);
            surface = Find.WorldGrid?.Surface;
            BuildBuckets();
            counts.Clear();
            base.GenerateFresh(seed, layer);
            buckets = null;
            Log.Message("[DepthRim] ocean biomes: " + string.Join(", ", System.Linq.Enumerable.Select(counts, kv => kv.Key + " " + kv.Value)));
        }

        protected override Tile GenerateTileFor(PlanetTile tile, PlanetLayer layer)
        {
            Tile result = base.GenerateTileFor(tile, layer);
            Vector3 dir = layer.GetTileCenter(tile).normalized;
            OceanBiome kind = OceanBiomeSelector.Choose(InputFor(dir));
            counts[kind] = counts.TryGetValue(kind, out int n) ? n + 1 : 1;
            BiomeDef biome = DefDatabase<BiomeDef>.GetNamedSilentFail(OceanBiomeSelector.DefName(kind)) ?? layer.Def.DefaultBiome;
            result.PrimaryBiome = biome;
            WaterMapExtension ext = biome.GetModExtension<WaterMapExtension>();
            List<Hilliness> hills = ext?.hilliness;
            result.hilliness = hills != null && hills.Count > 0 ? hills.RandomElement() : Hilliness.LargeHills;
            result.temperature = biome.constantOutdoorTemperature ?? -2f;
            return result;
        }

        private OceanBiomeInput InputFor(Vector3 dir)
        {
            OceanBiomeInput input = new OceanBiomeInput
            {
                ventNoise = Sample(ventNoise, dir),
                trenchNoise = Sample(trenchNoise, dir),
                variationNoise = Sample(variationNoise, dir),
                surfaceTemperature = 10f
            };
            int above = ClosestSurfaceTile(dir);
            if (above < 0)
            {
                return input;
            }
            Tile t = surface[above];
            input.surfaceKnown = true;
            input.surfaceTemperature = t.temperature;
            input.surfaceIce = t.PrimaryBiome == BiomeDefOf.IceSheet || t.PrimaryBiome == BiomeDefOf.SeaIce;
            input.surfaceWater = t.WaterCovered;
            surface.GetTileNeighbors(above, tmpNeighbors);
            for (int i = 0; i < tmpNeighbors.Count; i++)
            {
                if (surface[tmpNeighbors[i]].WaterCovered != input.surfaceWater)
                {
                    input.coastal = true;
                    break;
                }
            }
            return input;
        }

        private static float Sample(Perlin noise, Vector3 dir)
        {
            Vector3 p = dir * 100f;
            return Mathf.Clamp01((float)(noise.GetValue(p.x, p.y, p.z) * 0.5 + 0.5));
        }

        private void BuildBuckets()
        {
            buckets = null;
            if (surface == null || surface.Tiles == null || surface.Tiles.Count == 0)
            {
                return;
            }
            buckets = new List<int>[LonBuckets * LatBuckets];
            for (int i = 0; i < surface.TilesCount; i++)
            {
                int b = BucketOf(surface.GetTileCenter(i).normalized, out _, out _);
                (buckets[b] ?? (buckets[b] = new List<int>())).Add(i);
            }
        }

        private static int BucketOf(Vector3 dir, out int lon, out int lat)
        {
            float longitude = Mathf.Atan2(dir.z, dir.x) / (2f * Mathf.PI) + 0.5f;
            float latitude = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) / Mathf.PI + 0.5f;
            lon = Mathf.Clamp((int)(longitude * LonBuckets), 0, LonBuckets - 1);
            lat = Mathf.Clamp((int)(latitude * LatBuckets), 0, LatBuckets - 1);
            return lat * LonBuckets + lon;
        }

        /// <summary>方向 dir 正上方（最近）的地表格；地表資料不存在時回傳 -1。</summary>
        private int ClosestSurfaceTile(Vector3 dir)
        {
            if (buckets == null)
            {
                return -1;
            }
            BucketOf(dir, out int lon, out int lat);
            int best = -1;
            float bestDot = -2f;
            // 靠近兩極時經度桶很窄，多看幾格
            int lonRange = lat <= 2 || lat >= LatBuckets - 3 ? LonBuckets / 2 : 2;
            for (int dl = -1; dl <= 1; dl++)
            {
                int la = lat + dl;
                if (la < 0 || la >= LatBuckets)
                {
                    continue;
                }
                for (int dk = -lonRange; dk <= lonRange; dk++)
                {
                    int lo = ((lon + dk) % LonBuckets + LonBuckets) % LonBuckets;
                    List<int> list = buckets[la * LonBuckets + lo];
                    if (list == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < list.Count; i++)
                    {
                        float d = Vector3.Dot(surface.GetTileCenter(list[i]).normalized, dir);
                        if (d > bestDot)
                        {
                            bestDot = d;
                            best = list[i];
                        }
                    }
                }
            }
            return best;
        }
    }
}
