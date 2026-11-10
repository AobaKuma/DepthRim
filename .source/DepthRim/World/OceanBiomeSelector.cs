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
        MidOceanRidge
    }

    /// <summary>決定一個海洋格子的生態區與深度所需的海底地形資訊（世界生成在地表格網上算好）與雜訊（0–1）。</summary>
    public struct OceanBiomeInput
    {
        /// <summary>找得到正上方的地表格。找不到時只用雜訊（例如舊存檔補生成時地表資料不完整），視為遠洋。</summary>
        public bool surfaceKnown;

        /// <summary>地表格是冰原或海冰：冰層之下是海（原本的冰洋），不算陸塊。</summary>
        public bool surfaceIce;

        /// <summary>地表格是水（海洋、湖泊）。</summary>
        public bool surfaceWater;

        /// <summary>離海岸幾格（地表格子數）：陸地（含冰原）0、緊鄰陸地的水 1，往外遞增。</summary>
        public int coastDistance;

        /// <summary>離中洋脊軸線幾格：軸線上 0；這個洋盆沒有中洋脊時是 <see cref="OceanBiomeSelector.NoRidge"/>。</summary>
        public int ridgeDistance;

        /// <summary>最近的海岸是活動大陸邊緣（隱沒帶）：大陸棚窄、大陸坡陡，坡腳外是海溝。否則是被動大陸邊緣。</summary>
        public bool activeMargin;

        /// <summary>地表年均溫（°C）。</summary>
        public float surfaceTemperature;

        /// <summary>熱液雜訊：中洋脊軸線上，值高的段落是熱液噴口區。</summary>
        public float ventNoise;

        /// <summary>海山雜訊：深海平原上很高的值是海山（冷水珊瑚長在海山頂上）。</summary>
        public float seamountNoise;

        /// <summary>最近的海岸是冰原：這片水緊貼冰棚，也算結冰區（冰蓋邊緣犬牙交錯的範圍）。</summary>
        public bool nearIceSheet;

        /// <summary>冰緣雜訊（高頻碎形）：結冰區裡決定極地冰蓋從海岸往外伸多遠（<see cref="OceanBiomeSelector.IceReach"/>）。</summary>
        public float iceEdgeNoise;
    }

    /// <summary>
    /// 海洋層生態區與海床深度的規則，比照真實的海底地形（2026-11-09 使用者要求：中洋脊、近海隱沒帶海溝）。
    /// 純函式，可單元測試；海底地形資訊（離岸距離、中洋脊、活動邊緣）在 <see cref="WorldGenStep_OceanTiles"/> 算好。
    /// <list type="number">
    /// <item>正上方是冰原（陸上的冰）→ 極地冰蓋（冰棚下的海，升降井從這裡下去）；其他陸地 → 陸塊（沒有海洋）。</item>
    /// <item>結冰區（海冰、年均溫 &lt; −10 °C 的海、最近海岸是冰原的水）：極地冰蓋從海岸往外伸的距離由冰緣雜訊決定，
    /// 所以冰蓋與深洋盆的交界犬牙交錯：冰舌伸進洋盆，洋盆的海灣也切進冰蓋直到海岸。再往外照一般的海底地形（浮冰底下的洋盆一樣深）。</item>
    /// <item>被動大陸邊緣：大陸棚（離岸 1–2 格）→ 淺珊瑚礁／海藻林／極地冰蓋（依溫度）；大陸坡（3–4 格）→ 深珊瑚礁。</item>
    /// <item>活動大陸邊緣：大陸棚只有 1 格；大陸坡 2–3 格；坡腳外 4–5 格是平行海岸的深淵海溝。</item>
    /// <item>遠洋：中洋脊軸線 → 熱液噴口區（熱液段）或中洋脊；軸線兩側 1 格 → 中洋脊；海山 → 深珊瑚礁；其他 → 海床平原。</item>
    /// </list>
    /// </summary>
    public static class OceanBiomeSelector
    {
        public const float PolarTemperature = -10f;

        /// <summary>淺珊瑚礁要的地表年均溫（熱帶海岸）。</summary>
        public const float ReefTemperature = 20f;

        /// <summary>海藻林要的最低地表年均溫（太冷就是極地）。</summary>
        public const float KelpMinTemperature = -2f;

        /// <summary>被動邊緣的大陸棚最外一格、大陸坡最外一格。</summary>
        public const int ShelfTiles = 2;

        public const int SlopeTiles = 4;

        /// <summary>活動邊緣：大陸棚、大陸坡最外一格；海溝的兩格。</summary>
        public const int ActiveShelfTiles = 1;

        public const int ActiveSlopeTiles = 3;

        public const int TrenchInner = 4;

        public const int TrenchOuter = 5;

        /// <summary>中洋脊兩側算作中洋脊生態區的寬度（格）。</summary>
        public const int RidgeFlankTiles = 1;

        /// <summary>中洋脊軸線上熱液段的門檻。</summary>
        public const float VentThreshold = 0.6f;

        /// <summary>深海平原上的海山門檻（很稀少）。</summary>
        public const float SeamountThreshold = 0.93f;

        /// <summary>海山離中洋脊至少幾格（貼著脊的不算海山）。</summary>
        public const int SeamountMinRidgeDistance = 3;

        /// <summary>冰舌最多伸出大陸棚外幾格。</summary>
        public const int MaxIceTongueTiles = 8;

        /// <summary>冰緣雜訊的對比拉伸：低於下限的冰蓋縮到海岸（洋盆切到岸邊），高於上限的伸到最遠。</summary>
        public const float IceEdgeLow = 0.25f;

        public const float IceEdgeHigh = 0.75f;

        /// <summary>沒有中洋脊時的離脊距離。</summary>
        public const int NoRidge = 9999;

        /// <summary>沒有地表資料時的離岸距離（當作遠洋）。</summary>
        public const int MaxCoastDistance = 99;

        /// <summary>這格水在結冰區：海冰、極寒的海面，或最近的海岸是冰原。</summary>
        public static bool InFrozenZone(OceanBiomeInput input)
        {
            return input.surfaceKnown && input.surfaceWater
                   && (input.surfaceIce || input.surfaceTemperature < PolarTemperature || input.nearIceSheet);
        }

        /// <summary>
        /// 結冰區裡極地冰蓋從海岸往外伸的格數（0 到大陸棚 + <see cref="MaxIceTongueTiles"/>）。
        /// 冰緣雜訊是高頻碎形，所以沿著海岸忽遠忽近：遠的地方是伸進洋盆的冰舌，0 的地方洋盆切到岸邊。
        /// </summary>
        public static int IceReach(OceanBiomeInput input)
        {
            float t = (input.iceEdgeNoise - IceEdgeLow) / (IceEdgeHigh - IceEdgeLow);
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            int shelf = input.activeMargin ? ActiveShelfTiles : ShelfTiles;
            return (int)(t * (shelf + MaxIceTongueTiles + 0.999f));
        }

        /// <summary>
        /// 結冰區的冰緣：回傳這格是不是極地冰蓋（冰舌或冰棚）。不是的話，落在大陸棚／大陸坡範圍內的格子
        /// 是切進冰蓋的洋盆海灣，離岸距離當成大陸坡外一格，照遠洋規則（深）。
        /// </summary>
        private static bool IceEdge(OceanBiomeInput input, ref int dist, int slope)
        {
            if (!InFrozenZone(input))
            {
                return false;
            }
            if (dist <= IceReach(input))
            {
                return true;
            }
            if (dist <= slope)
            {
                dist = slope + 1;
            }
            return false;
        }

        public static OceanBiome Choose(OceanBiomeInput input)
        {
            if (input.surfaceKnown && !input.surfaceWater)
            {
                // 冰原（陸上的冰）之下是冰棚下的海：升降井從這裡下去；其他陸地沒有海洋
                return input.surfaceIce ? OceanBiome.PolarIceCap : OceanBiome.Landmass;
            }
            int dist = input.surfaceKnown ? input.coastDistance : MaxCoastDistance;
            int shelf = input.activeMargin ? ActiveShelfTiles : ShelfTiles;
            int slope = input.activeMargin ? ActiveSlopeTiles : SlopeTiles;
            // 結冰區：冰蓋與深洋盆犬牙交錯；浮冰底下的外海照一般的海底地形（北冰洋其實很深）
            if (IceEdge(input, ref dist, slope))
            {
                return OceanBiome.PolarIceCap;
            }
            if (dist <= shelf)
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
            if (input.ridgeDistance <= RidgeFlankTiles)
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
                return Lerp(100f, 500f, noise); // 冰原之下（冰棚）
            }
            int shelfTiles = input.activeMargin ? ActiveShelfTiles : ShelfTiles;
            if (IceEdge(input, ref dist, input.activeMargin ? ActiveSlopeTiles : SlopeTiles) && dist > shelfTiles)
            {
                return Lerp(200f, 800f, noise); // 伸出大陸棚的冰舌：冰下的淺灘
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
            float basin = BasinDepth(input.ridgeDistance) + Lerp(-250f, 250f, noise);
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
        public static float BasinDepth(int ridgeDistance)
        {
            if (ridgeDistance >= NoRidge)
            {
                return 5600f;
            }
            // 衰減長度 8 格：中洋脊是一條很寬的隆起（真實約上千公里寬），不是一道細線
            return 2500f + 3100f * (1f - (float)System.Math.Exp(-ridgeDistance / 8.0));
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
