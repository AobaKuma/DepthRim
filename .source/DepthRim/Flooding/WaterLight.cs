using Verse;

namespace DepthRim
{
    /// <summary>
    /// 讀地圖取得「水面有多少光」——公式在 <see cref="FloodingMath.CombineLight"/>。
    ///
    /// <para>外海層的頂點光照、暗處提示的判斷都走這裡，所以「看起來很暗」和「出現暗處提示」
    /// 永遠是同一個定義，不會一個算日光、一個不算。</para>
    /// </summary>
    public static class WaterLight
    {
        /// <summary>
        /// 人造光（0–1）：只有燈，沒有日光。網格頂點只存這個——燈變動時原版會標記 GroundGlow 讓區塊重建，
        /// 但日光隨時間一直在變、不會標記重建，烘進頂點會讓不同時間重建的區塊明暗不一（長時間遊玩後出現整塊 17×17 的色差）。
        /// </summary>
        public static float Artificial(Map map, IntVec3 c)
        {
            return map == null ? 0f : FloodingMath.LightOf(map.glowGrid.VisualGlowAt(c));
        }

        /// <summary>這一格看不看得到天空（沒屋頂 = 1）。網格頂點存它，日光強度由著色器的 <c>_SkyLight</c> 每幀乘上。</summary>
        public static float SkyExposure(Map map, IntVec3 c)
        {
            return map == null || map.roofGrid.Roofed(c) ? 0f : 1f;
        }

        /// <summary>目前的日光強度（0–1），每幀設給材質的 <c>_SkyLight</c>；和 <see cref="FloodingMath.CombineLight"/> 的日光項同一個公式。</summary>
        public static float SkyLevel(Map map, float ambientReach)
        {
            return map == null ? 0f : FloodingMath.CombineLight(map.skyManager.CurSkyGlow, ambientReach * DepthRimModSettings.Current.ambientScale, new UnityEngine.Color32(0, 0, 0, 0), false);
        }

        /// <summary>某一格的光照（0–1）。<paramref name="ambientReach"/> 來自該層級的 Def，會再乘上玩家設定。</summary>
        public static float At(Map map, IntVec3 c, float ambientReach)
        {
            if (map == null)
            {
                return 0f;
            }
            return FloodingMath.CombineLight(
                map.skyManager.CurSkyGlow,
                ambientReach * DepthRimModSettings.Current.ambientScale,
                map.glowGrid.VisualGlowAt(c),
                map.roofGrid.Roofed(c));
        }
    }
}
