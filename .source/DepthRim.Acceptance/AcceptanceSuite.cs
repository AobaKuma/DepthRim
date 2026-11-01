using System.Collections.Generic;

namespace DepthRim.Acceptance
{
    /// <summary>所有驗收測試，依序執行（後面的測試沿用 E1 生成的冰洋地圖）。</summary>
    public static class AcceptanceSuite
    {
        public static List<AcceptanceTest> Build()
        {
            return new List<AcceptanceTest>
            {
                new AcceptanceTest("L1", "Ocean layer exists with varied biomes, zooms from the surface, has no travel connection", Tests_World.L1_OceanLayer),
                new AcceptanceTest("E1", "Ice shaft generates an ocean-layer map with a linked landing", Tests_World.E1_IceShaft),
                new AcceptanceTest("F1", "Open water stays at 100%", Tests_Flooding.F1_OpenWater),
                new AcceptanceTest("F2", "A new sealed compartment starts flooded and a pump drains it", Tests_Flooding.F2_SealAndDrain),
                new AcceptanceTest("F3", "A damaged wall leaks into a drained compartment", Tests_Flooding.F3_DamagedWallLeaks),
                new AcceptanceTest("F4", "An open door floods the compartment from open water", Tests_Flooding.F4_OpenDoor),
                new AcceptanceTest("F5", "A destroyed wall turns the compartment into open water", Tests_Flooding.F5_Breach),
                new AcceptanceTest("S1", "Water levels survive Scribe save and load", Tests_Flooding.S1_ScribeRoundTrip),
                new AcceptanceTest("P1", "A colonist without gear drowns in open water", Tests_Pawns.P1_Drowning),
                new AcceptanceTest("P2", "A diving suit supplies air and refills out of the water", Tests_Pawns.P2_DivingSuit),
                new AcceptanceTest("P3", "Wading hediff follows the water level; no barotrauma in the shallows", Tests_Pawns.P3_WadingAndPressure),
                new AcceptanceTest("V1", "Water shaders compile; open water is drawn by the outer layer only", Tests_Visual.V1_ShaderLoaded),
                new AcceptanceTest("V2", "Compartment layer level/shore/no bleed; outer layer cuts the compartment out", Tests_Visual.V2_CompartmentEdges),
                new AcceptanceTest("V3", "Outer layer covers fog without leaking terrain and rebuilds when fog lifts", Tests_Visual.V3_OuterOverFog),
                new AcceptanceTest("V4", "Light is written into water vertices (cross-section, fog-safe), the suit head lamp works, rebuild cost", Tests_Visual.V4_Lighting),
                new AcceptanceTest("V5", "Dark-water hints for player pawns only; gone under a lamp", Tests_Visual.V5_DarkWaterHints),
                new AcceptanceTest("V6", "Per-tier water materials; the debug override rebuilds the whole map", Tests_Visual.V6_TierMaterials),
                new AcceptanceTest("V7", "Four tiers side by side: screenshots and frame rate", Tests_Visual.V7_TierScreenshots),
                new AcceptanceTest("V8", "A flooded compartment recolours per tier without D-stage effects", Tests_Visual.V8_CompartmentTiers),
                new AcceptanceTest("V9", "Abyss floor: terrain not drawn, backdrop mesh, rim weight, full water, impassable", Tests_Visual.V9_Abyss),
                new AcceptanceTest("V10", "Performance stress: 20 lit sections, 30 pawn hints, abyss backdrop rebuild", Tests_Visual.V10_Performance),
                new AcceptanceTest("V11", "Abyss carve branch at deep tier, Odyssey cliff edges, terrain restored", Tests_Phase2.V11_AbyssCarveAndCliffs),
                new AcceptanceTest("V12", "Mod settings: WriteSettings rebuilds materials, values apply, window renders", Tests_Phase2.V12_ModSettings),
                new AcceptanceTest("V13", "Underwater audio: muffle by wetness and listener, pump loop, tier ambience, inflow, creak, breach", Tests_Audio.V13_UnderwaterAudio),
                new AcceptanceTest("V14", "Sky light is not baked into the outer mesh; half-flooded compartments stay visible", Tests_Phase3.V14_SkyLightNotBaked),
                new AcceptanceTest("V15", "Watertight door stops dry non-divers; leaks spray at the inflow point", Tests_Phase3.V15_WatertightDoorAndLeaks),
                new AcceptanceTest("V16", "Each of the seven ocean biomes generates a map with its tier, rocks, kelp, vents and abyss", Tests_Ocean.V16_OceanBiomeMaps),
                new AcceptanceTest("V17", "Pressure hull wall leaks a tenth as much; the ice shaft only reaches shallow and mid-depth biomes", Tests_Ocean.V17_HullWallAndShaftLimit),
                new AcceptanceTest("V18", "Wall gaps filled; roof holes flood gradually; leak effects and sound follow wall durability", Tests_Phase4.V18_GapsRoofFillAndLeakStages),
                new AcceptanceTest("V19", "Water level line in the bottom-left readout; undersea weather on the right", Tests_Phase4.V19_ReadoutAndWeather),
                new AcceptanceTest("R1", "Water-line materials are created on the main thread and used for pawns in water", Tests_Pawns.R1_WaterLineMaterial),
            };
        }
    }
}
