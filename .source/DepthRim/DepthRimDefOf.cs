using RimWorld;
using Verse;

namespace DepthRim
{
    [DefOf]
    public static class DepthRimDefOf
    {
        public static PlanetLayerDef DepthRim_Ocean;

        public static PlanetLayerSettingsDef DepthRim_Ocean_Settings;

        public static WorldObjectDef DepthRim_OceanSite;

        /// <summary>海洋層的陸塊：大陸下方，沒有海洋。</summary>
        public static BiomeDef DepthRim_Landmass;

        // ── 水位系統 ──
        public static HediffDef DepthRim_Wading;

        public static HediffDef DepthRim_Drowning;

        public static HediffDef DepthRim_Barotrauma;

        /// <summary>魚離水：倒地並很快窒息（<see cref="FishHabitat"/>）。</summary>
        public static HediffDef DepthRim_FishOutOfWater;

        public static StatDef DepthRim_PressureResistance;

        /// <summary>水下 AI：缺氧或睡覺處淹水時游到安全處（<see cref="JobDriver_SeekAir"/>）。</summary>
        public static JobDef DepthRim_SeekAir;

        // ── 壓力適應與加壓艙 ──
        public static HediffDef DepthRim_PressureAdaptation;

        /// <summary>船體平台與耐壓甲板都提供的承載力：加壓艙的地板要有它。</summary>
        public static TerrainAffordanceDef DepthRim_HullPlatform;

        public static ThingDef DepthRim_Pressurizer;

        // ── 水體渲染 ──
        public static ShaderTypeDef DepthRim_WaterVolume;

        public static ShaderTypeDef DepthRim_WaterOuter;

        public static MapMeshFlagDef DepthRim_Water;

        /// <summary>深淵地板（二期 P2-10）：不畫地形，改由深淵背景層補上。</summary>
        public static TerrainDef DepthRim_Abyss;

        // ── 水中音效（二期 P2-11）──
        public static SoundDef DepthRim_HullCreak;

        public static SoundDef DepthRim_Breach;

        public static SoundDef DepthRim_FloodLoop;

        public static SoundDef DepthRim_PumpLoop;

        public static SoundDef DepthRim_DiverBubbles;

        public static SoundDef DepthRim_DiverLowAir;

        /// <summary>加壓艙的門打開、兩側壓差夠大時的洩壓嘶聲。</summary>
        public static SoundDef DepthRim_PressureHiss;

        // ── 水密門與進水效果 ──
        public static ThingDef DepthRim_WatertightDoor;

        public static FleckDef DepthRim_LeakSpray;

        // ── 中層水域（地形下方經過的生物）──
        public static ShaderTypeDef DepthRim_Midwater;

        public static IncidentDef DepthRim_GiantPasses;

        // ── 海面地圖 ──
        /// <summary>海面：類虛空地形，不畫，海面由 SectionLayer_SeaSurface 畫成背景。</summary>
        public static TerrainDef DepthRim_OpenSea;

        public static TerrainDef DepthRim_OceanPlatform;

        public static ThingDef DepthRim_PlatformPillar;

        public static WorldObjectDef DepthRim_SeaSurfaceSite;

        // ── 海面的兩層（水面層／平台層）──
        /// <summary>平台層的高空：看下去是海。</summary>
        public static TerrainDef DepthRim_OpenAir;

        public static MapGeneratorDef DepthRim_PlatformLevel;

        public static ThingDef DepthRim_PlatformPillarTop;

        // ── 破冰上浮 ──
        public static WorldObjectDef DepthRim_IceBreachSite;

        public static TerrainDef DepthRim_SeaIceSheet;

        public static TerrainDef DepthRim_IceCracked;

        public static TerrainDef DepthRim_IceRubble;

        public static TerrainDef DepthRim_NewIce;

        public static TerrainDef DepthRim_BreachWater;

        public static ThingDef DepthRim_Filth_IceChunks;

        public static MapMeshFlagDef DepthRim_LowerLevel;

        // ── 安全繩錨點 ──
        public static ThingDef DR_RopeAnchor;

        /// <summary>航行中掛在安全繩上被水流拖著：移動、操作變差。</summary>
        public static HediffDef DepthRim_TetheredInCurrent;

        static DepthRimDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(DepthRimDefOf));
        }
    }
}
