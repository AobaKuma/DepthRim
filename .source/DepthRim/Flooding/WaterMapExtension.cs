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

        /// <summary>
        /// 不挖深淵的層級（淺海、變溫層）上，另外挖幾個圓形的深淵坑：潛艇只能在深淵上的船體平台建造，
        /// 升降井通到的極地冰蓋需要有地方當船塢。0 = 不挖。
        /// </summary>
        public IntRange abyssPockets = IntRange.Zero;

        /// <summary>深淵坑的半徑（格）。</summary>
        public FloatRange abyssPocketRadius = new FloatRange(9f, 13f);

        /// <summary>地圖生成時散佈的熱液泉數量（熱液噴口區；之後接 P2-7 的專用熱液泉）。</summary>
        public IntRange hydrothermalVents = IntRange.Zero;

        /// <summary>地圖生成時散佈在海床上的天然物件（海底火山、玄武岩塊等），由 <see cref="GenStep_OceanFeatures"/> 放置。</summary>
        public List<SeafloorFeature> seafloorFeatures = new List<SeafloorFeature>();

        /// <summary>世界格子的地勢候選：決定岩體與礦脈多寡（海床平原平坦、珊瑚礁與海溝起伏大）。</summary>
        public List<Hilliness> hilliness = new List<Hilliness> { Hilliness.LargeHills, Hilliness.Mountainous };

        /// <summary>
        /// 世界格子的海床深度（公尺，海面以下）。世界生成依深度雜訊在範圍內取值，存成負的 <c>Tile.elevation</c>：
        /// 地形分頁的「高度」顯示例如 −4200m，世界地圖的地形也照這個高度著色。
        /// </summary>
        public FloatRange seafloorDepth = new FloatRange(200f, 600f);

        /// <summary>深度雜訊 t（0–1）對應的格子高度：海面以下，所以是負數。</summary>
        public float ElevationAt(float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return -(seafloorDepth.min + (seafloorDepth.max - seafloorDepth.min) * t);
        }

        /// <summary>升降井能不能通到這個生態區：只有淺海與變溫層（鑽探深度上限；更深的海域靠潛艇）。</summary>
        public bool ShaftAccessible => depthTier <= DepthTier.Thermocline;
    }

    /// <summary><see cref="WaterMapExtension.seafloorFeatures"/> 的一項：要散佈的物件與數量。</summary>
    public class SeafloorFeature
    {
        public ThingDef thing;

        public IntRange count = IntRange.One;

        /// <summary>佔地周圍要留空（可站立、沒有建築）的格數：大型物件留 1 格，岩塊 0 格就能貼著彼此。</summary>
        public int clearance = 1;

        /// <summary>離地圖邊緣至少幾格。</summary>
        public int edgeMargin = 8;
    }

    /// <summary>地圖父物件實作此介面時，由它決定目前水深層級（例如潛艇依下潛深度）。</summary>
    public interface IDepthTierProvider
    {
        DepthTier CurrentDepthTier { get; }
    }

    public static class FloodingUtility
    {
        /// <summary>
        /// 整張地圖的水體網格重建。地圖還在生成中（繪製器的區塊還沒建立，例如 DMSE 起飛時新建的潛艇地圖）就跳過：
        /// 原版 <c>MapDrawer.WholeMapChanged</c> 不檢查這種情況會丟 NullReferenceException，而之後的整張重建本來就會照新狀態畫。
        /// </summary>
        public static void WaterChanged(Map map)
        {
            if (map?.mapDrawer?.GlobalLayers == null)
            {
                return;
            }
            map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
        }

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
            return comp != null && comp.HasWater ? comp.LevelAt(c) : 0f;
        }
    }
}
