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
        Run("WaterCorners", WaterCorners);
        Run("OuterCoverage", OuterCoverage);
        Run("FloodingVisual", FloodingVisual);
        Run("OpenFillAndLeakStages", OpenFillAndLeakStages);
        Run("AudioMath", AudioMath);
        Run("WatertightDoor", WatertightDoor);
        Run("OceanBiomes", OceanBiomes);
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
