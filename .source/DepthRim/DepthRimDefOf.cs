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

        public static StatDef DepthRim_PressureResistance;

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

        // ── 水密門與進水效果 ──
        public static ThingDef DepthRim_WatertightDoor;

        public static FleckDef DepthRim_LeakSpray;

        // ── 中層水域（地形下方經過的生物）──
        public static ShaderTypeDef DepthRim_Midwater;

        public static IncidentDef DepthRim_GiantPasses;

        static DepthRimDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(DepthRimDefOf));
        }
    }
}
