using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 深海／海床地圖產生深淵區域（二期 P2-10）：用 Perlin 雜訊挖出不規則的深洞，
    /// 讓「深淵地板」出現在**真實的遊戲路徑**上，而不是只有驗收測試或除錯動作造得出來。
    ///
    /// <para>只在地圖層級是深海或海床時作用（<see cref="FloodingMath.CarvesAbyss"/>）；
    /// 淺海與變溫層要看得到海床，所以不挖。有屋頂的格子（岩頂）也不動——
    /// 深淵是「沒有頂的洞」，不是礦坑。</para>
    /// </summary>
    public class GenStep_AbyssCarve : GenStep
    {
        /// <summary>雜訊尺度：越小越大片。0.045 大約是 20 格級的團塊。</summary>
        private const float NoiseScale = 0.045f;

        public override int SeedPart => 771204318;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (map == null)
            {
                return;
            }
            WaterMapExtension ext = map.Biome?.GetModExtension<WaterMapExtension>();
            DepthTier tier = FloodingUtility.DepthTierOf(map);
            Carve(map, tier, Rand.Range(0f, 1000f), Rand.Range(0f, 1000f), ext?.abyssBias ?? 0f);
            if (ext != null && !FloodingMath.CarvesAbyss(tier) && ext.abyssPockets.max > 0)
            {
                CarvePockets(map, ext.abyssPockets.RandomInRange, ext.abyssPocketRadius);
            }
        }

        /// <summary>
        /// 依層級在地圖上挖出深淵，回傳挖了幾格。拆成公開方法是為了讓驗收測試（V11）能在淺海的測試地圖上
        /// 以指定層級執行同一段程式；真正的地圖生成走 <see cref="Generate"/>，層級來自地圖父物件。
        /// <paramref name="ox"/>／<paramref name="oz"/> 是雜訊取樣偏移，每張地圖不同，避免深淵長在同一個位置。
        /// </summary>
        public static int Carve(Map map, DepthTier tier, float ox, float oz, float bias = 0f)
        {
            if (!FloodingMath.CarvesAbyss(tier))
            {
                // 為什麼要記這一行：沒有它，「這個步驟沒被執行」和「執行了但這個層級不需要挖」
                // 在 log 裡長得一模一樣（都是沒有訊息），而那種模糊正是最難查的一類問題。
                Log.Message($"[DepthRim] abyss carve skipped on tier {tier} for map {map.uniqueID}");
                return 0;
            }
            int carved = 0;
            foreach (IntVec3 c in map.AllCells)
            {
                if (c.Roofed(map))
                {
                    continue;
                }
                TerrainDef terrain = map.terrainGrid.TerrainAt(c);
                if (terrain == null || terrain.dontRender || terrain == DepthRimDefOf.DepthRim_Abyss)
                {
                    continue;
                }
                float noise = Mathf.PerlinNoise(ox + c.x * NoiseScale, oz + c.z * NoiseScale);
                // 生態區偏移（深淵海溝）：把雜訊整體抬高，過門檻的區域變大
                if (!FloodingMath.ShouldCarveAbyss(tier, noise + bias))
                {
                    continue;
                }
                map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_Abyss);
                carved++;
            }
            if (carved > 0)
            {
                Log.Message($"[DepthRim] carved {carved} abyss cell(s) for map {map.uniqueID} (tier {tier})");
            }
            return carved;
        }
    
        /// <summary>坑邊緣的雜訊尺度：越大邊緣越破碎。</summary>
        private const float PocketNoiseScale = 0.18f;

        /// <summary>
        /// 一格在不在深淵坑裡（純函式，可單元測試）：離中心的距離小於半徑 × (0.8 + 0.4 × 雜訊)，
        /// 雜訊 0..1，所以邊緣在 0.8～1.2 倍半徑之間起伏，不是正圓。
        /// </summary>
        public static bool InPocket(float distance, float radius, float noise)
        {
            return distance <= radius * (0.8f + 0.4f * noise);
        }

        /// <summary>
        /// 在不挖深淵的層級上挖 <paramref name="count"/> 個深淵坑（給潛艇當船塢）。坑心離地圖邊緣至少半徑 + 12 格；
        /// 一樣跳過有屋頂的格子（岩體），所以坑只會開在開放水域。回傳挖了幾格。
        /// </summary>
        public static int CarvePockets(Map map, int count, FloatRange radiusRange)
        {
            int carved = 0;
            float ox = Rand.Range(0f, 1000f);
            float oz = Rand.Range(0f, 1000f);
            for (int p = 0; p < count; p++)
            {
                float radius = radiusRange.RandomInRange;
                int margin = Mathf.CeilToInt(radius * 1.2f) + 12;
                if (map.Size.x <= margin * 2 || map.Size.z <= margin * 2)
                {
                    break;
                }
                IntVec3 center = new IntVec3(Rand.RangeInclusive(margin, map.Size.x - margin - 1), 0, Rand.RangeInclusive(margin, map.Size.z - margin - 1));
                foreach (IntVec3 c in GenRadial.RadialCellsAround(center, radius * 1.2f, true))
                {
                    if (!c.InBounds(map) || c.Roofed(map))
                    {
                        continue;
                    }
                    TerrainDef terrain = map.terrainGrid.TerrainAt(c);
                    if (terrain == null || terrain.dontRender || terrain == DepthRimDefOf.DepthRim_Abyss)
                    {
                        continue;
                    }
                    float noise = Mathf.PerlinNoise(ox + c.x * PocketNoiseScale, oz + c.z * PocketNoiseScale);
                    if (!InPocket(c.DistanceTo(center), radius, noise))
                    {
                        continue;
                    }
                    map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_Abyss);
                    carved++;
                }
            }
            if (carved > 0)
            {
                Log.Message($"[DepthRim] carved {count} abyss pocket(s), {carved} cell(s), for map {map.uniqueID}");
            }
            return carved;
        }
    }
}
