using System.Diagnostics;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>
    /// 驗收測試用：以 <c>-depthrim-coverage=1</c> 啟動時，把 quicktest 寫死的世界覆蓋率（30%）換成指定值，
    /// 用來測 100% 覆蓋率下的海洋層生成；並記錄整個世界生成花的時間。只在驗收測試期間載入。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CoverageOverride
    {
        public const string ArgKey = "depthrim-coverage";

        public static float? Coverage { get; private set; }

        public static double WorldGenSeconds { get; private set; }

        private static Stopwatch watch;

        static CoverageOverride()
        {
            if (!AcceptanceDriver.Enabled)
            {
                return;
            }
            if (GenCommandLine.TryGetCommandLineArg(ArgKey, out string value) && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float c))
            {
                Coverage = UnityEngine.Mathf.Clamp(c, 0.05f, 1f);
            }
            Harmony harmony = new Harmony("DepthRim.Acceptance.Coverage");
            harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GenerateWorld)),
                prefix: new HarmonyMethod(typeof(CoverageOverride), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(CoverageOverride), nameof(Postfix)));
        }

        public static void Prefix(ref float planetCoverage)
        {
            if (Coverage.HasValue)
            {
                planetCoverage = Coverage.Value;
            }
            Log.Message($"[DepthRim acceptance] generating world at {planetCoverage:P0} coverage");
            watch = Stopwatch.StartNew();
        }

        public static void Postfix()
        {
            WorldGenSeconds = watch?.Elapsed.TotalSeconds ?? 0;
            Log.Message($"[DepthRim acceptance] world generated in {WorldGenSeconds:0.0} s");
        }
    }
}
