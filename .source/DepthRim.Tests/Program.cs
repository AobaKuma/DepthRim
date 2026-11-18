using System;

static partial class Test
{
    public static int Main()
    {
        Run("FloodingLeak", FloodingLeak);
        Run("FloodingDoorFlow", FloodingDoorFlow);
        Run("FloodingVolume", FloodingVolume);
        Run("FloodingPumpDrainTime", FloodingPumpDrainTime);
        Run("Pressure", Pressure);
        Run("WaterRenderCut", WaterRenderCut);
        Run("DivingAir", DivingAir);
        Run("SeekAirRules", SeekAirRules);
        Run("WaterCorners", WaterCorners);
        Run("OuterCoverage", OuterCoverage);
        Run("FloodingVisual", FloodingVisual);
        Run("OpenFillAndLeakStages", OpenFillAndLeakStages);
        Run("AudioMath", AudioMath);
        Run("WatertightDoor", WatertightDoor);
        Run("OceanBiomes", OceanBiomes);
        Run("OceanTemperature", OceanTemperatureTests);
        Run("MidwaterMath", MidwaterMathTests);
        Run("ContentMidwater", ContentMidwater);
        Run("FishVariantSelection", FishVariantSelection);
        Run("ContentFish", ContentFish);
        Run("FishHabitatRules", FishHabitatRules);
        Run("ContentHullPlatform", ContentHullPlatform);
        Run("ContentDesignationCategory", ContentDesignationCategory);
        Run("ContentSeaSurface", ContentSeaSurface);
        Run("SeaSwayMath", SeaSwayMath);
        Run("VoyageViewMath", VoyageViewMath);
        Run("CenterVoyageHull", CenterVoyageHull);
        Run("SubmarineDive", SubmarineDive);
        Run("SubmarineRouting", SubmarineRouting);
        Run("SubmarinePockets", SubmarinePockets);
        Run("ContentSubmarine", ContentSubmarine);
        Run("ContentSubmarinePrefab", ContentSubmarinePrefab);
        Run("IceBreachMath", IceBreachMathTests);
        Run("IceCrackPattern", IceCrackPatternTests);
        Run("SurfacingCurves", SurfacingCurvesTests);
        Run("WallPressure", WallPressure);
        Run("PressureAdaptation", PressureAdaptation);
        Run("BoardingReadiness", BoardingReadiness);
        Run("RopeAnchorMath", RopeAnchorMath);
        Run("Gangway", Gangway);
        Run("SeaWeather", SeaWeather);
        Run("ContentResearch", ContentResearch);
        Run("SeafloorSampler", SeafloorSampler);
        Run("LandmarkMath", LandmarkMathTests);
        Run("ContentLandmark", ContentLandmark);
        Run("BoardingCraftFuel", BoardingCraftFuelTests);
        Run("UnderwaterRange", UnderwaterRange);
        Run("ImplosionMath", ImplosionMathTests);
        Run("ContentImplosion", ContentImplosion);
        Run("ContentPressure", ContentPressure);
        Run("GenePressureMath", GenePressureMath);
        Run("GeneBreathing", GeneBreathing);
        Run("SeasicknessMath", SeasicknessMathTests);
        Run("ContentGenes", ContentGenes);
        Run("ContentXml", ContentXml);
        Run("ContentOcean", ContentOcean);
        Run("ContentSounds", ContentSounds);
        Run("ContentDefFields", ContentDefFields);
        Run("ContentDefOf", ContentDefOf);
        Run("ContentShaders", ContentShaders);
        Run("ContentWaterTiers", ContentWaterTiers);
        Run("ContentTypes", ContentTypes);
        Run("ContentKeyed", ContentKeyed);
        Run("ContentDefInjected", ContentDefInjected);
        Run("HarmonyTargets", HarmonyTargets);
        foreach (string failure in failures) { Console.WriteLine("FAIL: " + failure); }
        Console.WriteLine($"{(failures.Count == 0 ? "PASS" : "FAILED")}: {count} DepthRim checks passed, {failures.Count} failed (real game/DepthRim assemblies).");
        return failures.Count == 0 ? 0 : 1;
    }
}
