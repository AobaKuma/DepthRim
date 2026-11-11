using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;
using K = DepthRim.OceanTemperature.Kind;

// 海洋水溫（2026-11-11）：依深度與正上方氣溫，季節／日夜變化隨深度衰減；地下冰洋固定冰點、熱液區偏暖且起伏。
static partial class Test
{
    static void OceanTemperatureTests()
    {
        // 海面水溫跟著氣溫，但不低於冰點、不超過 29.5
        Check(OceanTemperature.SurfaceWater(-30f) == OceanTemperature.FreezingPoint, "polar surface water bottoms out at seawater's freezing point");
        Check(OceanTemperature.SurfaceWater(45f) == OceanTemperature.MaxSurfaceWater, "tropical surface water tops out near 29.5 °C");
        Check(OceanTemperature.SurfaceWater(27f) > 24f && OceanTemperature.SurfaceWater(12f) > 10f && OceanTemperature.SurfaceWater(12f) < 16f, "surface water tracks the air temperature (tropical ~25 °C, temperate ~13 °C)");

        // 深度剖面：混合層、斜溫層、深層水
        float tropical = 27f;
        Check(Math.Abs(OceanTemperature.AnnualMean(10f, tropical, K.OpenWater) - OceanTemperature.AnnualMean(45f, tropical, K.OpenWater)) < 0.01f, "the mixed layer (top 50 m) is one temperature");
        float[] profile = new[] { 50f, 200f, 500f, 1000f, 2000f, 4000f }.Select(d => OceanTemperature.AnnualMean(d, tropical, K.OpenWater)).ToArray();
        Check(profile.Zip(profile.Skip(1), (a, b) => b < a).All(x => x), "water gets colder with depth through the thermocline");
        Check(profile[3] > 3.5f && profile[3] < 7f, $"about 4–6 °C at 1000 m ({profile[3]:0.0})");
        Check(OceanTemperature.AnnualMean(4000f, tropical, K.OpenWater) < 3.5f && OceanTemperature.AnnualMean(4000f, -20f, K.OpenWater) > 0f, "the deep ocean is 1–3 °C everywhere");
        Check(OceanTemperature.AnnualMean(4000f, tropical, K.OpenWater) > OceanTemperature.AnnualMean(4000f, -20f, K.OpenWater), "deep water under the tropics is slightly warmer than under the poles");

        // 季節與日夜振幅隨深度衰減
        Check(OceanTemperature.SeasonalFactor(0f, K.OpenWater) < 0.5f && OceanTemperature.SeasonalFactor(0f, K.OpenWater) > 0.3f, "surface water swings with the seasons, but less than the air");
        Check(OceanTemperature.SeasonalFactor(20f, K.OpenWater) > OceanTemperature.SeasonalFactor(200f, K.OpenWater) * 5f, "the seasonal swing dies out below the seasonal thermocline");
        Check(OceanTemperature.SeasonalFactor(3000f, K.OpenWater) < 0.001f, "the deep ocean has no seasons");
        Check(OceanTemperature.DailyFactor(5f, K.OpenWater) > 0f && OceanTemperature.DailyFactor(5f, K.OpenWater) < 0.2f && OceanTemperature.DailyFactor(200f, K.OpenWater) < 0.0001f, "only the top few tens of meters feel day and night, and only a little");

        // 地下冰洋：固定冰點，沒有季節與日夜
        Check(OceanTemperature.AnnualMean(30f, 5f, K.Subglacial) == OceanTemperature.FreezingPoint, "water right under the sea ice sits at the freezing point");
        Check(OceanTemperature.AnnualMean(3000f, 5f, K.Subglacial) > OceanTemperature.FreezingPoint && OceanTemperature.AnnualMean(3000f, 5f, K.Subglacial) <= OceanTemperature.SubglacialDeepWater + 0.01f, "deep subglacial water is a little warmer (polar deep water, about -0.5 °C)");
        Check(OceanTemperature.SeasonalFactor(10f, K.Subglacial) == 0f && OceanTemperature.DailyFactor(10f, K.Subglacial) == 0f && OceanTemperature.DailyRandomFactor(10f, K.Subglacial) == 0f, "the ice sheet insulates the subglacial ocean: no seasons, no day-night swing");

        // 熱液區：比周圍深層水暖，而且起伏
        float vent = OceanTemperature.AnnualMean(2600f, 20f, K.Hydrothermal), around = OceanTemperature.AnnualMean(2600f, 20f, K.OpenWater);
        Check(vent - around > 4f && vent < 15f, $"hydrothermal fields are a few degrees warmer than the deep water around them ({vent:0.0} vs {around:0.0} °C)");
        Check(OceanTemperature.DailyRandomFactor(2600f, K.Hydrothermal) >= 0.3f && OceanTemperature.SeasonalFactor(2600f, K.Hydrothermal) < 0.001f, "vent output pulses (daily swings) but has no seasons");

        // 季節偏移不會讓水溫低於冰點
        Check(Math.Abs(OceanTemperature.ClampSeasonalOffset(-1f, -5f) - (OceanTemperature.FreezingPoint + 1f)) < 0.0001f && OceanTemperature.ClampSeasonalOffset(10f, -3f) == -3f, "a seasonal dip never takes water below freezing");

        // 生態區不再用固定溫度
        string biomes = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Defs", "World", "Ocean_Biomes.xml"));
        Check(!biomes.Contains("constantOutdoorTemperature"), "ocean biomes no longer use a fixed outdoor temperature");
    }
}
