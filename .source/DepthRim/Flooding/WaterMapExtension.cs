using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 掛在 <see cref="BiomeDef"/> 上：這個生態區的地圖「室外就是水」，啟用 <see cref="MapComponent_Flooding"/>。
    /// 冰洋層的地圖與之後的潛艇地圖都用這個生態區。
    /// </summary>
    public class WaterMapExtension : DefModExtension
    {
        /// <summary>新地圖、尚未計算過的格子的初始水位（0–1）。</summary>
        public float initialLevel = 1f;

        /// <summary>
        /// 這個海洋生態區的地圖位在哪個水深層級（海床在多深）。決定水壓、水體視覺與深淵生成；
        /// 地圖父物件實作 <see cref="IDepthTierProvider"/>（潛艇）時以父物件為準。
        /// </summary>
        public DepthTier depthTier = DepthTier.Shallow;

        /// <summary>加在深淵雜訊上的偏移：越大挖得越多（深淵海溝用）。0 = 依層級的預設門檻。</summary>
        public float abyssBias = 0f;

        /// <summary>地圖生成時散佈的熱液泉數量（熱液噴口區；之後接 P2-7 的專用熱液泉）。</summary>
        public IntRange hydrothermalVents = IntRange.Zero;

        /// <summary>世界格子的地勢候選：決定岩體與礦脈多寡（海床平原平坦、珊瑚礁與海溝起伏大）。</summary>
        public List<Hilliness> hilliness = new List<Hilliness> { Hilliness.LargeHills, Hilliness.Mountainous };

        /// <summary>升降井能不能通到這個生態區：只有淺海與變溫層（目前的潛水服只撐得住水壓 1）。</summary>
        public bool ShaftAccessible => depthTier <= DepthTier.Thermocline;
    }

    /// <summary>地圖父物件實作此介面時，由它決定目前水深層級（例如潛艇依下潛深度）。</summary>
    public interface IDepthTierProvider
    {
        DepthTier CurrentDepthTier { get; }
    }

    public static class FloodingUtility
    {
        public static bool IsWaterMap(Map map)
        {
            return map?.Biome != null && map.Biome.HasModExtension<WaterMapExtension>();
        }

        /// <summary>地圖目前的水深層級：潛艇等父物件優先，否則看生態區（<see cref="WaterMapExtension.depthTier"/>）。</summary>
        public static DepthTier DepthTierOf(Map map)
        {
            if (map?.Parent is IDepthTierProvider provider)
            {
                return provider.CurrentDepthTier;
            }
            return map?.Biome?.GetModExtension<WaterMapExtension>()?.depthTier ?? DepthTier.Shallow;
        }

        public static MapComponent_Flooding Flooding(this Map map)
        {
            return map?.GetComponent<MapComponent_Flooding>();
        }

        /// <summary>此格水位（0–1）；非水下地圖一律 0。</summary>
        public static float WaterLevelAt(this IntVec3 c, Map map)
        {
            MapComponent_Flooding comp = map.Flooding();
            return comp != null && comp.Active ? comp.LevelAt(c) : 0f;
        }
    }
}
