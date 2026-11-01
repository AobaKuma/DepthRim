namespace DepthRim
{
    /// <summary>海洋層的七種生態區。</summary>
    public enum OceanBiome : byte
    {
        PolarIceCap,
        SeabedPlain,
        ShallowReef,
        DeepReef,
        KelpForest,
        HydrothermalField,
        AbyssalTrench
    }

    /// <summary>決定一個海洋格子是哪種生態區所需的地表資訊（正上方的地表格）與三個確定性雜訊（0–1）。</summary>
    public struct OceanBiomeInput
    {
        /// <summary>找得到正上方的地表格。找不到時只用雜訊（例如舊存檔補生成時地表資料不完整）。</summary>
        public bool surfaceKnown;

        /// <summary>地表格是冰（冰原、海冰）或年均溫低於 <see cref="OceanBiomeSelector.PolarTemperature"/>。</summary>
        public bool surfaceIce;

        /// <summary>地表格是水（海洋、湖泊）。</summary>
        public bool surfaceWater;

        /// <summary>地表格在海岸線上：水格旁邊有陸地，或陸地旁邊有水。</summary>
        public bool coastal;

        /// <summary>地表年均溫（°C）。</summary>
        public float surfaceTemperature;

        /// <summary>熱點雜訊：很高的值才是熱液噴口區。</summary>
        public float ventNoise;

        /// <summary>海溝雜訊：落在 0.5 附近的細帶才是海溝，所以海溝是長條狀。</summary>
        public float trenchNoise;

        /// <summary>變化雜訊：遠洋裡深珊瑚礁和海床平原的分界。</summary>
        public float variationNoise;
    }

    /// <summary>
    /// 海洋層生態區的分配規則（使用者選定「依正上方地表決定」）。純函式，可單元測試；
    /// 世界生成的取值在 <see cref="WorldGenStep_OceanTiles"/>。
    /// <list type="number">
    /// <item>正上方是冰（冰原、海冰、極寒）→ 極地冰蓋（原本的冰洋）。</item>
    /// <item>熱點雜訊很高、而且不在海岸 → 熱液噴口區。</item>
    /// <item>海溝雜訊落在中線細帶、而且不在海岸 → 深淵海溝。</item>
    /// <item>海岸：溫暖 → 淺珊瑚礁；涼爽 → 海藻林。</item>
    /// <item>遠洋／內陸下方：溫暖且變化雜訊高 → 深珊瑚礁，否則海床平原。</item>
    /// </list>
    /// </summary>
    public static class OceanBiomeSelector
    {
        public const float PolarTemperature = -10f;

        /// <summary>淺珊瑚礁要的地表年均溫（熱帶海岸）。</summary>
        public const float ReefTemperature = 20f;

        /// <summary>海藻林要的最低地表年均溫（太冷就是極地）。</summary>
        public const float KelpMinTemperature = -2f;

        /// <summary>深珊瑚礁要的最低地表年均溫。</summary>
        public const float DeepReefTemperature = 8f;

        public const float VentThreshold = 0.9f;

        public const float TrenchHalfWidth = 0.025f;

        public const float DeepReefVariation = 0.6f;

        public static OceanBiome Choose(OceanBiomeInput input)
        {
            if (input.surfaceKnown && (input.surfaceIce || input.surfaceTemperature < PolarTemperature))
            {
                return OceanBiome.PolarIceCap;
            }
            if (!input.coastal && input.ventNoise >= VentThreshold)
            {
                return OceanBiome.HydrothermalField;
            }
            if (!input.coastal && System.Math.Abs(input.trenchNoise - 0.5f) < TrenchHalfWidth)
            {
                return OceanBiome.AbyssalTrench;
            }
            if (input.surfaceKnown && input.coastal)
            {
                if (input.surfaceTemperature >= ReefTemperature)
                {
                    return OceanBiome.ShallowReef;
                }
                if (input.surfaceTemperature >= KelpMinTemperature)
                {
                    return OceanBiome.KelpForest;
                }
                return OceanBiome.PolarIceCap;
            }
            if (input.surfaceTemperature >= DeepReefTemperature && input.variationNoise >= DeepReefVariation)
            {
                return OceanBiome.DeepReef;
            }
            return OceanBiome.SeabedPlain;
        }

        /// <summary>生態區對應的 BiomeDef defName。</summary>
        public static string DefName(OceanBiome biome)
        {
            return "DepthRim_" + biome;
        }
    }
}
