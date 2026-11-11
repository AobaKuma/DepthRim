namespace DepthRim
{
    /// <summary>海洋層的生態區（加上大陸下方的陸塊：那裡沒有海洋）。</summary>
    public enum OceanBiome : byte
    {
        PolarIceCap,
        SeabedPlain,
        ShallowReef,
        DeepReef,
        KelpForest,
        HydrothermalField,
        AbyssalTrench,
        /// <summary>正上方是陸地（冰原除外）：沒有海洋，世界地圖上畫成正上方的陸地。</summary>
        Landmass,
        /// <summary>中洋脊：洋盆中線上隆起的火山山脈，比兩側的深海平原淺。</summary>
        MidOceanRidge,
        /// <summary>地下冰洋（變溫層，200–2000 m）。淺層是 <see cref="PolarIceCap"/>。</summary>
        SubglacialThermocline,
        /// <summary>地下冰洋（深海，2100–6000 m）。</summary>
        SubglacialDeep,
        /// <summary>地下冰洋（深海海床，6000 m 以上：冰海下的海溝）。</summary>
        SubglacialSeabed
    }

    /// <summary>決定一個海洋格子的生態區與深度所需的海底地形資訊（世界生成在地表格網上算好）與雜訊（0–1）。</summary>
    public struct OceanBiomeInput
    {
        /// <summary>找得到正上方的地表格。找不到時只用雜訊（例如舊存檔補生成時地表資料不完整），視為遠洋。</summary>
        public bool surfaceKnown;

        /// <summary>地表格是海冰（冰海）：地下冰洋只長在冰海下。冰原（陸上的冰）是陸地，不算。</summary>
        public bool surfaceIce;

        /// <summary>地表格是水（海洋、湖泊）。</summary>
        public bool surfaceWater;

        /// <summary>離海岸幾格（地表格子數）：陸地（含冰原）0、緊鄰陸地的水 1，往外遞增。</summary>
        public int coastDistance;

        /// <summary>離中洋脊軸線幾格：軸線上 0；這個洋盆沒有中洋脊時是 <see cref="OceanBiomeSelector.NoRidge"/>。</summary>
        public int ridgeDistance;

        /// <summary>
        /// 這段中洋脊的寬度（-0.5 到 0.5，在最近的軸線格取樣）：0 是平均寬度，正的較寬、負的較窄。
        /// 決定脊兩側算作中洋脊生態區的格數（<see cref="OceanBiomeSelector.RidgeFlank"/>）與隆起的衰減長度。
        /// </summary>
        public float ridgeWidth;

        /// <summary>最近的海岸是活動大陸邊緣（隱沒帶）：大陸棚窄、大陸坡陡，坡腳外是海溝。否則是被動大陸邊緣。</summary>
        public bool activeMargin;

        /// <summary>地表年均溫（°C）。</summary>
        public float surfaceTemperature;

        /// <summary>熱液雜訊：中洋脊軸線上，值高的段落是熱液噴口區。</summary>
        public float ventNoise;

        /// <summary>海山雜訊：深海平原上很高的值是海山（冷水珊瑚長在海山頂上）。</summary>
        public float seamountNoise;
    }

    /// <summary>
    /// 海洋層生態區與海床深度的規則，比照真實的海底地形（2026-11-09 使用者要求：中洋脊、近海隱沒帶海溝）。
    /// 純函式，可單元測試；海底地形資訊（離岸距離、中洋脊、活動邊緣）在 <see cref="WorldGenStep_OceanTiles"/> 算好。
    /// <list type="number">
    /// <item>陸地（含冰原：陸上的冰算陸地）→ 陸塊（沒有海洋）。</item>
    /// <item>地下冰洋（defName 沿用 DepthRim_PolarIceCap，舊存檔相容）只長在冰海（海冰）下（2026-11-11 使用者要求）：
    /// 地表是冰海的格子，下方必定是地下冰洋（同日使用者定案），不分離岸遠近。深度照一般海底地形，
    /// 再依深度分成淺海／變溫層／深海／深海海床四個生態區（<see cref="SubglacialFor"/>）。升降井開在冰海上，只到得了前兩層。</item>
    /// <item>被動大陸邊緣：大陸棚（離岸 1–2 格）→ 淺珊瑚礁（熱帶）或海藻林；大陸坡（3–4 格）→ 深珊瑚礁。</item>
    /// <item>活動大陸邊緣：大陸棚只有 1 格；大陸坡 2–3 格；坡腳外 4–5 格是平行海岸的深淵海溝。</item>
    /// <item>遠洋：中洋脊軸線 → 熱液噴口區（熱液段）或中洋脊；軸線兩側 1 格 → 中洋脊；海山 → 深珊瑚礁；其他 → 海床平原。</item>
    /// </list>
    /// </summary>
    public static class OceanBiomeSelector
    {
        public const float PolarTemperature = -10f;

        /// <summary>淺珊瑚礁要的地表年均溫（熱帶海岸）。</summary>
        public const float ReefTemperature = 20f;

        /// <summary>被動邊緣的大陸棚最外一格、大陸坡最外一格。</summary>
        public const int ShelfTiles = 2;

        public const int SlopeTiles = 4;

        /// <summary>活動邊緣：大陸棚、大陸坡最外一格；海溝的兩格。</summary>
        public const int ActiveShelfTiles = 1;

        public const int ActiveSlopeTiles = 3;

        public const int TrenchInner = 4;

        public const int TrenchOuter = 5;

        /// <summary>中洋脊兩側算作中洋脊生態區的平均寬度（格）；實際寬度沿著脊在 0 到 <see cref="MaxRidgeFlankTiles"/> 之間變化。</summary>
        public const int RidgeFlankTiles = 1;

        public const int MaxRidgeFlankTiles = 3;

        /// <summary>這段中洋脊兩側算作中洋脊生態區的格數：寬度 0 時是 <see cref="RidgeFlankTiles"/>，窄段 0（只剩軸線）、寬段最多 3。</summary>
        public static int RidgeFlank(float ridgeWidth)
        {
            int flank = (int)System.Math.Round(RidgeFlankTiles + 0.25f + ridgeWidth * 6f, System.MidpointRounding.AwayFromZero);
            return flank < 0 ? 0 : flank > MaxRidgeFlankTiles ? MaxRidgeFlankTiles : flank;
        }

        /// <summary>中洋脊軸線上熱液段的門檻。</summary>
        public const float VentThreshold = 0.6f;

        /// <summary>深海平原上的海山門檻（很稀少）。</summary>
        public const float SeamountThreshold = 0.93f;

        /// <summary>海山離中洋脊至少幾格（貼著脊的不算海山）。</summary>
        public const int SeamountMinRidgeDistance = 3;

        /// <summary>沒有中洋脊時的離脊距離。</summary>
        public const int NoRidge = 9999;

        /// <summary>沒有地表資料時的離岸距離（當作遠洋）。</summary>
        public const int MaxCoastDistance = 99;

        /// <summary>這格水在結冰區：正上方是冰海（海冰）。這裡下方必定是地下冰洋，別處都不會有。</summary>
        public static bool InFrozenZone(OceanBiomeInput input)
        {
            return input.surfaceKnown && input.surfaceWater && input.surfaceIce;
        }

        /// <summary>地下冰洋各層的深度上限（m）：淺海 / 變溫層 / 深海；更深是深海海床。</summary>
        public const float SubglacialShallowMax = 200f;

        public const float SubglacialThermoclineMax = 2100f; // 和中洋脊、熱液區的深度下限一致（變溫層的深度範圍到 2000 m）

        public const float SubglacialDeepMax = 6000f; // 洋盆（深海平原）最深約 5850 m，更深的只有海溝

        /// <summary>這個生態區是地下冰洋（任一層）。</summary>
        public static bool IsSubglacial(OceanBiome biome)
        {
            return biome == OceanBiome.PolarIceCap || biome == OceanBiome.SubglacialThermocline || biome == OceanBiome.SubglacialDeep || biome == OceanBiome.SubglacialSeabed;
        }

        /// <summary>這個 defName 是地下冰洋（任一層）。</summary>
        public static bool IsSubglacial(string defName)
        {
            return defName == "DepthRim_PolarIceCap" || defName == "DepthRim_SubglacialThermocline" || defName == "DepthRim_SubglacialDeep" || defName == "DepthRim_SubglacialSeabed";
        }

        /// <summary>依海床深度選地下冰洋的層（<see cref="Choose"/> 對冰海回傳淺層 <see cref="OceanBiome.PolarIceCap"/>，深度算好後再換）。</summary>
        public static OceanBiome SubglacialFor(float depth)
        {
            if (depth < SubglacialShallowMax)
            {
                return OceanBiome.PolarIceCap;
            }
            if (depth < SubglacialThermoclineMax)
            {
                return OceanBiome.SubglacialThermocline;
            }
            return depth < SubglacialDeepMax ? OceanBiome.SubglacialDeep : OceanBiome.SubglacialSeabed;
        }

        public static OceanBiome Choose(OceanBiomeInput input)
        {
            if (input.surfaceKnown && !input.surfaceWater)
            {
                // 陸地（含冰原）下方沒有海洋
                return OceanBiome.Landmass;
            }
            int dist = input.surfaceKnown ? input.coastDistance : MaxCoastDistance;
            int shelf = input.activeMargin ? ActiveShelfTiles : ShelfTiles;
            int slope = input.activeMargin ? ActiveSlopeTiles : SlopeTiles;
            // 冰海下必定是地下冰洋
            if (InFrozenZone(input))
            {
                return OceanBiome.PolarIceCap;
            }
            if (dist <= shelf)
            {
                if (input.surfaceTemperature >= ReefTemperature)
                {
                    return OceanBiome.ShallowReef;
                }
                // 沒有海冰的寒冷大陸棚也是海藻林（真實的北極也有海藻林）；地下冰洋只長在冰海下
                return OceanBiome.KelpForest;
            }
            if (dist <= slope)
            {
                return OceanBiome.DeepReef;
            }
            if (input.activeMargin && dist <= TrenchOuter)
            {
                return OceanBiome.AbyssalTrench;
            }
            if (input.ridgeDistance == 0)
            {
                return input.ventNoise >= VentThreshold ? OceanBiome.HydrothermalField : OceanBiome.MidOceanRidge;
            }
            if (input.ridgeDistance <= RidgeFlank(input.ridgeWidth))
            {
                return OceanBiome.MidOceanRidge;
            }
            if (input.seamountNoise >= SeamountThreshold && input.ridgeDistance >= SeamountMinRidgeDistance)
            {
                return OceanBiome.DeepReef;
            }
            return OceanBiome.SeabedPlain;
        }

        /// <summary>
        /// 海床深度（公尺）。noise（0–1）在每一段的範圍內取值。
        /// <list type="bullet">
        /// <item>大陸棚、大陸坡依離岸距離（活動邊緣的棚窄坡陡）；活動邊緣坡腳外是海溝（軸線最深）。</item>
        /// <item>遠洋：中洋脊軸線約 2500 m，離脊越遠越深（海洋地殼冷卻下沉），趨近 5600 m；沒有中洋脊的洋盆是 5600 m。
        /// 被動邊緣的大陸隆堤（離岸 5–7 格）從 3000 m 漸漸過渡到這個深度。</item>
        /// <item>海山頂 700–1500 m。</item>
        /// </list>
        /// </summary>
        public static float SeafloorDepth(OceanBiomeInput input, float noise)
        {
            noise = noise < 0f ? 0f : noise > 1f ? 1f : noise;
            int dist = input.surfaceKnown ? input.coastDistance : MaxCoastDistance;
            if (dist <= 0)
            {
                return Lerp(100f, 500f, noise); // 陸地（陸塊不用這個深度，保險用）
            }
            if (input.activeMargin)
            {
                switch (dist)
                {
                    case 1: return Lerp(10f, 120f, noise);
                    case 2: return Lerp(250f, 1000f, noise);
                    case 3: return Lerp(1000f, 2000f, noise);
                    case 4: return Lerp(6000f, 8000f, noise);
                    case 5: return Lerp(7500f, 11000f, noise);
                }
            }
            else
            {
                switch (dist)
                {
                    case 1: return Lerp(10f, 80f, noise);
                    case 2: return Lerp(60f, 180f, noise);
                    case 3: return Lerp(250f, 700f, noise);
                    case 4: return Lerp(650f, 1250f, noise);
                }
            }
            if (input.ridgeDistance >= SeamountMinRidgeDistance && input.seamountNoise >= SeamountThreshold)
            {
                return Lerp(700f, 1500f, noise);
            }
            float basin = BasinDepth(input.ridgeDistance, input.ridgeWidth) + Lerp(-250f, 250f, noise);
            // 被動邊緣的大陸隆堤：離岸 5 格 3000 m，到 8 格過渡成洋盆深度
            int rise = input.activeMargin ? TrenchOuter + 1 : SlopeTiles + 1;
            if (dist < rise + 3)
            {
                float t = (dist - rise) / 3f;
                float start = input.activeMargin ? 5500f : 3000f;
                return Lerp(start, basin, t < 0f ? 0f : t);
            }
            return basin;
        }

        /// <summary>洋盆深度：中洋脊軸線 2500 m，離脊越遠越深（8 格約 4500 m），趨近 5600 m；沒有中洋脊時 5600 m。</summary>
        public static float BasinDepth(int ridgeDistance, float ridgeWidth = 0f)
        {
            if (ridgeDistance >= NoRidge)
            {
                return 5600f;
            }
            // 衰減長度平均 8 格：中洋脊是一條很寬的隆起（真實約上千公里寬），不是一道細線；
            // 沿著脊在 4 到 14 格之間變化（慢速擴張的脊窄而陡，快速擴張的寬而緩）
            double decay = 8.0 + 16.0 * ridgeWidth;
            decay = decay < 4.0 ? 4.0 : decay > 14.0 ? 14.0 : decay;
            return 2500f + 3100f * (1f - (float)System.Math.Exp(-ridgeDistance / decay));
        }

        /// <summary>海岸往外到第幾格，深度才完全是海底地形算出來的深度（之前和地表視圖的深度混合）。</summary>
        public const int SurfaceBlendTiles = 5;

        /// <summary>
        /// 地表視圖的深度在這格的權重（2026-11-11 使用者要求：靠近陸地邊緣的海洋深度趨近地表視圖，再往外漸變成海底地形的深度）。
        /// 緊鄰陸地（離岸 1 格）是 1，離岸 <see cref="SurfaceBlendTiles"/> 格以上是 0，中間平滑過渡；沒有地表資料時 0。
        /// </summary>
        public static float SurfaceBlendWeight(int coastDistance)
        {
            if (coastDistance <= 1)
            {
                return coastDistance < 1 ? 0f : 1f;
            }
            if (coastDistance >= SurfaceBlendTiles)
            {
                return 0f;
            }
            float t = (float)(SurfaceBlendTiles - coastDistance) / (SurfaceBlendTiles - 1);
            return t * t * (3f - 2f * t);
        }

        /// <summary>海床深度和地表視圖的水深（正上方地表格的負海拔，原版 0–500 m）依離岸距離混合。</summary>
        public static float BlendWithSurface(float seafloorDepth, float surfaceDepth, int coastDistance)
        {
            float w = SurfaceBlendWeight(coastDistance);
            return Lerp(seafloorDepth, surfaceDepth < 0f ? 0f : surfaceDepth, w);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        /// <summary>生態區對應的 BiomeDef defName。</summary>
        public static string DefName(OceanBiome biome)
        {
            return "DepthRim_" + biome;
        }
    }
}
