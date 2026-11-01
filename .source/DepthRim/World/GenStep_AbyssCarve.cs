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
            float bias = map.Biome?.GetModExtension<WaterMapExtension>()?.abyssBias ?? 0f;
            Carve(map, FloodingUtility.DepthTierOf(map), Rand.Range(0f, 1000f), Rand.Range(0f, 1000f), bias);
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
    }
}
