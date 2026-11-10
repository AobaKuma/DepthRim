using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace DepthRim
{
    /// <summary>
    /// 海洋層的格子：依正上方的地表格與海底地形骨架（<see cref="SeafloorGeometry"/>：離岸距離、中洋脊、活動大陸邊緣）
    /// 決定生態區（<see cref="OceanBiomeSelector.Choose"/>）與海床深度（<see cref="OceanBiomeSelector.SeafloorDepth"/>），
    /// 再依生態區給地勢（岩體與礦脈多寡，來自 <see cref="WaterMapExtension.hilliness"/>）與溫度。
    /// 陸地（冰原、海冰除外）正下方是陸塊：沒有海洋，地勢、溫度與高度照抄地表格。
    /// 地表層一定比海洋層先生成（<see cref="WorldGenerator"/> 依圖層 ID 依序生成，海洋層是後來註冊的）。
    /// </summary>
    public class WorldGenStep_OceanTiles : WorldGenStep_Tiles
    {
        public override int SeedPart => 734151820;

        private PlanetLayer surface;

        private SurfaceTileLookup lookup;

        private SeafloorGeometry geometry;

        private Perlin ventNoise;

        private Perlin seamountNoise;

        private Perlin depthNoise;

        private Perlin iceEdgeNoise;

        private readonly Dictionary<OceanBiome, int> counts = new Dictionary<OceanBiome, int>();

        public override void GenerateFresh(string seed, PlanetLayer layer)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            int s = GenText.StableStringHash(seed) ^ SeedPart;
            // 取樣點是半徑 100 的球面座標，頻率比照原版世界雜訊的量級（0.01–0.1）
            ventNoise = new Perlin(0.03, 2.0, 0.5, 3, s, QualityMode.Medium);
            seamountNoise = new Perlin(0.09, 2.0, 0.5, 2, s + 1, QualityMode.Medium);
            depthNoise = new Perlin(0.05, 2.0, 0.5, 4, s + 3, QualityMode.Medium);
            // 冰緣：高頻、高持續度的碎形，冰蓋邊緣才會犬牙交錯而不是平滑的弧線
            iceEdgeNoise = new Perlin(0.08, 2.2, 0.65, 5, s + 4, QualityMode.Medium);
            surface = Find.WorldGrid?.Surface;
            lookup = SurfaceTileLookup.Build(surface);
            geometry = lookup != null ? new SeafloorGeometry(surface, s) : null;
            double geometrySeconds = watch.Elapsed.TotalSeconds;
            counts.Clear();
            base.GenerateFresh(seed, layer);
            Log.Message("[DepthRim] ocean biomes: " + string.Join(", ", System.Linq.Enumerable.Select(counts, kv => kv.Key + " " + kv.Value))
                        + (geometry != null ? $"; ridge axis {geometry.RidgeAxisTiles} tiles, active-margin coast {geometry.ActiveCoastTiles} tiles" : "")
                        + $"; {layer.TilesCount} tiles in {watch.Elapsed.TotalSeconds:0.00} s (seafloor geometry {geometrySeconds:0.00} s)");
            lookup = null;
            geometry = null;
            surface = null;
        }

        protected override Tile GenerateTileFor(PlanetTile tile, PlanetLayer layer)
        {
            Tile result = base.GenerateTileFor(tile, layer);
            Vector3 dir = layer.GetTileCenter(tile).normalized;
            int above = SurfaceTileAbove(tile, layer, dir);
            OceanBiomeInput input = InputFor(dir, above);
            OceanBiome kind = OceanBiomeSelector.Choose(input);
            counts[kind] = counts.TryGetValue(kind, out int n) ? n + 1 : 1;
            BiomeDef biome = DefDatabase<BiomeDef>.GetNamedSilentFail(OceanBiomeSelector.DefName(kind)) ?? layer.Def.DefaultBiome;
            result.PrimaryBiome = biome;

            if (kind == OceanBiome.Landmass && above >= 0)
            {
                // 陸塊：照抄正上方的陸地（世界地圖畫成那塊陸地，地形分頁顯示它的高度）
                Tile land = surface[above];
                result.hilliness = land.hilliness == Hilliness.Undefined ? Hilliness.Flat : land.hilliness;
                result.temperature = land.temperature;
                result.elevation = Mathf.Max(land.elevation, 1f);
                return result;
            }

            WaterMapExtension ext = biome.GetModExtension<WaterMapExtension>();
            List<Hilliness> hills = ext?.hilliness;
            result.hilliness = hills != null && hills.Count > 0 ? hills.RandomElement() : Hilliness.LargeHills;
            result.temperature = biome.constantOutdoorTemperature ?? -2f;
            float depth = OceanBiomeSelector.SeafloorDepth(input, Sample(depthNoise, dir));
            if (ext != null)
            {
                // 生態區的深度範圍只是保險（例如極寒海面上的極地冰蓋維持淺海，升降井才到得了）
                depth = Mathf.Clamp(depth, ext.seafloorDepth.min, ext.seafloorDepth.max);
            }
            // 海床深度：世界地圖的地形分頁顯示，水深著色也用它（負值 = 海面以下）
            result.elevation = -depth;
            return result;
        }

        /// <summary>正上方的地表格：細分相同時格子 ID 一一對應，否則用分桶搜尋。</summary>
        private int SurfaceTileAbove(PlanetTile tile, PlanetLayer layer, Vector3 dir)
        {
            if (lookup == null)
            {
                return -1;
            }
            if (surface.TilesCount == layer.TilesCount && Vector3.Dot(surface.GetTileCenter(tile.tileId).normalized, dir) > 0.999999f)
            {
                return tile.tileId;
            }
            return lookup.ClosestTo(dir);
        }

        private OceanBiomeInput InputFor(Vector3 dir, int above)
        {
            OceanBiomeInput input = new OceanBiomeInput
            {
                ventNoise = Sample(ventNoise, dir),
                seamountNoise = Sample(seamountNoise, dir),
                iceEdgeNoise = Sample(iceEdgeNoise, dir),
                surfaceTemperature = 10f,
                coastDistance = OceanBiomeSelector.MaxCoastDistance,
                ridgeDistance = OceanBiomeSelector.NoRidge
            };
            if (above < 0)
            {
                return input;
            }
            Tile t = surface[above];
            input.surfaceKnown = true;
            input.surfaceTemperature = t.temperature;
            input.surfaceIce = t.PrimaryBiome == BiomeDefOf.IceSheet || t.PrimaryBiome == BiomeDefOf.SeaIce;
            input.surfaceWater = t.WaterCovered;
            input.coastDistance = geometry.coastDistance[above];
            input.ridgeDistance = geometry.ridgeDistance[above];
            input.activeMargin = geometry.activeMargin[above];
            int coast = geometry.nearestCoast[above];
            input.nearIceSheet = coast >= 0 && surface[coast].PrimaryBiome == BiomeDefOf.IceSheet;
            return input;
        }

        private static float Sample(Perlin noise, Vector3 dir)
        {
            Vector3 p = dir * 100f;
            return Mathf.Clamp01((float)(noise.GetValue(p.x, p.y, p.z) * 0.5 + 0.5));
        }
    }

    /// <summary>
    /// 依方向找最近的地表格。原版的 <c>GetClosestTile_NewTemp</c> 每次都做一次搜尋，十幾萬格太慢；
    /// 這裡先把地表格依經緯度分桶，再到所在的桶與相鄰桶找最近的格心。世界生成與海洋層的陸地繪製共用。
    /// </summary>
    public class SurfaceTileLookup
    {
        private const int LonBuckets = 360;

        private const int LatBuckets = 180;

        private readonly List<int>[] buckets;

        public PlanetLayer Surface { get; }

        private SurfaceTileLookup(PlanetLayer surface)
        {
            Surface = surface;
            buckets = new List<int>[LonBuckets * LatBuckets];
            for (int i = 0; i < surface.TilesCount; i++)
            {
                int b = BucketOf(surface.GetTileCenter(i).normalized, out _, out _);
                (buckets[b] ?? (buckets[b] = new List<int>())).Add(i);
            }
        }

        /// <summary>地表資料不存在時回傳 null。</summary>
        public static SurfaceTileLookup Build(PlanetLayer surface)
        {
            if (surface == null || surface.Tiles == null || surface.Tiles.Count == 0)
            {
                return null;
            }
            return new SurfaceTileLookup(surface);
        }

        private static int BucketOf(Vector3 dir, out int lon, out int lat)
        {
            float longitude = Mathf.Atan2(dir.z, dir.x) / (2f * Mathf.PI) + 0.5f;
            float latitude = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) / Mathf.PI + 0.5f;
            lon = Mathf.Clamp((int)(longitude * LonBuckets), 0, LonBuckets - 1);
            lat = Mathf.Clamp((int)(latitude * LatBuckets), 0, LatBuckets - 1);
            return lat * LonBuckets + lon;
        }

        /// <summary>方向 dir 正上方（最近）的地表格 ID。</summary>
        public int ClosestTo(Vector3 dir)
        {
            BucketOf(dir, out int lon, out int lat);
            int best = -1;
            float bestDot = -2f;
            // 靠近兩極時經度桶很窄，多看幾格
            int lonRange = lat <= 4 || lat >= LatBuckets - 5 ? LonBuckets / 2 : lat <= 20 || lat >= LatBuckets - 21 ? 12 : 2;
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
                        float d = Vector3.Dot(Surface.GetTileCenter(list[i]).normalized, dir);
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
