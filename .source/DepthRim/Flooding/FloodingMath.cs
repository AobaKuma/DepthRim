using UnityEngine;
// Verse 只用來取 IntVec3（純值型別 x/y/z）；FloodingMath 仍然不碰 Map、Room、Def。
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水位系統的純計算（不碰地圖與房間），讓 <see cref="MapComponent_Flooding"/>、<see cref="PawnWaterRendering"/>
    /// 與單元測試（.source/DepthRim.Tests）共用同一套公式。
    /// </summary>
    public static class FloodingMath
    {
        /// <summary>受損牆體每秒滲入的格數（完全損毀前的最大值，再乘水壓倍率與水位差）。</summary>
        public const float LeakCellsPerSecondPerWall = 0.6f;

        /// <summary>打開的門每秒交換的格數（乘水位差）。</summary>
        public const float DoorFlowCellsPerSecond = 25f;

        public const float LevelEpsilon = 0.002f;

        /// <summary>水壓倍率：淺海 1、變溫層 2、深海 4、海床 8。</summary>
        public static float PressureFactor(DepthTier tier)
        {
            return 1 << (int)tier;
        }

        /// <summary>沒入水中時需要的環境水壓抗性 = 水深層級（淺海 0 … 海床 3）。</summary>
        public static int RequiredPressureResistance(DepthTier tier)
        {
            return (int)tier;
        }

        /// <summary>和外海相通（碰到地圖邊緣）的格子每秒上升的水位，乘上水壓倍率。淺海約 20 秒從乾到滿；深海幾乎瞬間。</summary>
        public const float SeaFillPerSecond = 0.05f;

        /// <summary>只有屋頂破洞（沒碰到地圖邊緣）的房間：每一格破洞每秒灌進的水量（格數），乘上水壓倍率。</summary>
        public const float RoofInflowCellsPerSecond = 0.4f;

        /// <summary>
        /// 和外海相通的房間裡，一格沒滿的格子這次更新要上升多少（使用者回報「露天區要慢慢進水，不要馬上填滿」）。
        /// 碰到地圖邊緣：每格固定速度 × 水壓。只有屋頂破洞：破洞數 × 每格流量 × 水壓，平均分給還沒滿的格子，但不超過碰到邊緣時的速度。
        /// </summary>
        public static float OpenRoomFill(bool touchesEdge, int openRoofCells, int unfilledCells, DepthTier tier, float seconds)
        {
            if (unfilledCells <= 0)
            {
                return 0f;
            }
            float seaRate = SeaFillPerSecond * PressureFactor(tier) * seconds;
            if (touchesEdge)
            {
                return seaRate;
            }
            float volume = Mathf.Max(0, openRoofCells) * RoofInflowCellsPerSecond * PressureFactor(tier) * seconds;
            return Mathf.Min(seaRate, volume / unfilledCells);
        }

        /// <summary>
        /// 室內建築是否被淹沒（使用者要求「依 fill percent 決定建築是否出現水下效果」）：水位 ≥ 建築高度（fillPercent）就整個泡在水裡。
        /// 很矮的建築（fillPercent 接近 0）至少要有 <see cref="MinSubmergeLevel"/> 的水才算，避免一點積水就整片蓋上。
        /// </summary>
        public static bool BuildingSubmerged(float fillPercent, float level)
        {
            return level > LevelEpsilon && level >= Mathf.Max(MinSubmergeLevel, fillPercent);
        }

        public const float MinSubmergeLevel = 0.1f;

        /// <summary>
        /// 進水效果的強度階段（依牆體耐久，使用者要求「漏水效果的速度與強度要跟耐久階段相關」）：
        /// 0 = 滲水（耐久 &gt; 75%）、1 = 噴水（50–75%）、2 = 強噴（25–50%）、3 = 湧入（&lt; 25%）。
        /// </summary>
        public static int LeakStage(float hitPointFraction)
        {
            float hp = Mathf.Clamp01(hitPointFraction);
            if (hp > 0.75f) { return 0; }
            if (hp > 0.5f) { return 1; }
            if (hp > 0.25f) { return 2; }
            return 3;
        }

        /// <summary>某一階段每次噴幾團水花、尺寸、速度；越破越多、越大、越快。</summary>
        public static void LeakEffect(int stage, out int count, out float scale, out float speed)
        {
            stage = Mathf.Clamp(stage, 0, 3);
            count = stage + 1;
            scale = 0.4f + 0.25f * stage;
            speed = 0.35f + 0.55f * stage;
        }

        /// <summary>一面受損牆體在 seconds 秒內滲入的水量（格數）；另一側水位不高於本側時為 0。</summary>
        public static float LeakVolume(int hitPoints, int maxHitPoints, DepthTier tier, float sourceLevel, float level, float seconds)
        {
            if (maxHitPoints <= 0 || hitPoints >= maxHitPoints || sourceLevel <= level + LevelEpsilon)
            {
                return 0f;
            }
            float damage = 1f - (float)Mathf.Max(0, hitPoints) / maxHitPoints;
            return LeakCellsPerSecondPerWall * damage * PressureFactor(tier) * (sourceLevel - level) * seconds;
        }

        /// <summary>
        /// 打開的門在 seconds 秒內從高水位側流到低水位側的水量（格數）。
        /// 不超過「把較小房間拉到平均值」所需的量，避免單次更新來回震盪；開放水域的格數視為無限大。
        /// </summary>
        public static float DoorFlowVolume(float highLevel, float lowLevel, float highCells, float lowCells, float seconds)
        {
            float diff = highLevel - lowLevel;
            if (diff <= LevelEpsilon)
            {
                return 0f;
            }
            float smaller = Mathf.Min(highCells, lowCells);
            return Mathf.Min(DoorFlowCellsPerSecond * diff * seconds, diff * smaller * 0.5f);
        }

        /// <summary>把水量變化（格數）套用到房間水位上。</summary>
        public static float ApplyVolume(float level, float volumeDelta, int cellCount)
        {
            return Mathf.Clamp01(level + volumeDelta / Mathf.Max(1, cellCount));
        }

        public static byte ToByte(float level)
        {
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(level) * 255f);
        }

        public static float FromByte(byte b)
        {
            return b / 255f;
        }

        // ── 水體網格 ─────────────────────────────────────────────────────────

        /// <summary>水體網格頂點周圍一格的種類（見 <see cref="SectionLayer_WaterVolume"/>）。</summary>
        public enum CornerCell : byte
        {
            /// <summary>同一片水（同一間房間，或任一方是打開的門）：參與水位平均。</summary>
            SameWater,
            /// <summary>岩體、牆、關著的門：算入岸邊程度，不參與平均。</summary>
            Solid,
            /// <summary>另一間房間（例如斜角隔著牆）或地圖外：兩者都不算。</summary>
            Ignore
        }

        /// <summary>
        /// 一個頂點的水位與岸邊程度。<paramref name="kinds"/> 與 <paramref name="levels"/> 是共用這個頂點的四格。
        /// 水位只平均同一片水的格子，所以牆兩側的水位不會互相滲進對方；岸邊程度 = 實心格比例。
        /// 呼叫端保證畫這個頂點的那一格本身是 SameWater，所以平均的分母至少為 1。
        /// </summary>
        public static void WaterCorner(CornerCell[] kinds, float[] levels, out float level, out float shore)
        {
            float sum = 0f;
            int water = 0;
            int solid = 0;
            for (int i = 0; i < kinds.Length; i++)
            {
                switch (kinds[i])
                {
                    case CornerCell.SameWater:
                        sum += levels[i];
                        water++;
                        break;
                    case CornerCell.Solid:
                        solid++;
                        break;
                }
            }
            level = water > 0 ? sum / water : 0f;
            shore = kinds.Length > 0 ? (float)solid / kinds.Length : 0f;
        }

        /// <summary>
        /// 外海層（畫在迷霧之上）是否涵蓋這一格：
        /// <list type="bullet">
        /// <item>迷霧下的格子一律涵蓋，而且不看底下是什麼（不能洩漏未探索的地形）。</item>
        /// <item>已探索的實心格（岩體、牆）：涵蓋，除非它緊鄰已探索的密閉艙室（艙室的牆保持清楚）。</item>
        /// <item>已探索的空格：開放水域才涵蓋；密閉艙室交給艙室層。</item>
        /// </list>
        /// </summary>
        public static bool OuterIncludes(bool fogged, bool solid, bool openWater, bool bordersCompartment)
        {
            if (fogged)
            {
                return true;
            }
            if (solid)
            {
                return !bordersCompartment;
            }
            return openWater;
        }

        // ── 水體視覺（設計稿 §3.1–§3.3、實作指南 A/B/B2） ─────────────────────

        /// <summary>
        /// 波紋重複週期（格）：著色器把世界座標乘上取樣尺度，所以尺度 0.03 是每 33.3 格重複一次。
        /// 尺度 ≤ 0 時回傳 0（沒有波紋）。
        /// </summary>
        public static float RipplePeriodCells(float scale)
        {
            return scale <= 0f ? 0f : 1f / scale;
        }

        /// <summary>單一格的光照亮度：原版 <c>GlowGrid.VisualGlowAt</c> 的最亮通道（和光照覆蓋層同一個來源）。</summary>
        public static float LightOf(Color32 glow)
        {
            int max = Mathf.Max(glow.r, Mathf.Max(glow.g, glow.b));
            return max / 255f;
        }

        /// <summary>
        /// 水面「有多少光」：日光與人造光取大者。
        ///
        /// <para><b>日光</b>用原版 <see cref="Verse.GlowGrid.GroundGlowAt(IntVec3)"/> 的取法：無屋頂時
        /// <see cref="Verse.SkyManager.CurSkyGlow"/>，所以白天開放水域一定「有光」，和左下角顯示的「光度」一致。
        /// 再乘上該層級的 <paramref name="ambientReach"/>：日光本來就穿不深，深海白天才不會變透明。</para>
        ///
        /// <para><b>人造光</b>刻意用 <see cref="LightOf"/> 的原始值（0–1 平滑衰減），**不套**原版給遊戲判定用的
        /// 「×3.6、上限 0.5」——那個轉換會把燈光壓成一片 0.5 的硬邊圓盤，B 階段靠的
        /// 「燈下 0.796、3 格外 0.353、10 格外 0」的漸層就沒了。人造光也不乘 ambientReach：燈就掛在水裡。</para>
        ///
        /// <para>純函式，不讀地圖；實際取值的包裝在 <see cref="WaterLight.At"/>。</para>
        /// </summary>
        public static float CombineLight(float skyGlow, float ambientReach, Color32 glow, bool roofed)
        {
            float ambient = roofed ? 0f : Mathf.Clamp01(skyGlow * Mathf.Clamp01(ambientReach));
            return Mathf.Clamp01(Mathf.Max(ambient, LightOf(glow)));
        }

        /// <summary>
        /// 深淵格離最近「非深淵格」的距離（切比雪夫，一格一跳）：非深淵格回 0，
        /// 深淵格回 1..<paramref name="maxCells"/>，<paramref name="maxCells"/> 圈內都找不到就回上限。
        ///
        /// <para>給深淵背景層當頂點色的 r 通道：緊鄰崖壁的一圈看得到崖壁往下延伸，
        /// 超過 4 格就是全黑（二期 P2-10）。純函式，用委派判斷哪些格子是深淵，才能單元測試。</para>
        /// </summary>
        public static int AbyssRimDistance(IntVec3 cell, int maxCells, System.Func<IntVec3, bool> isAbyss)
        {
            if (isAbyss == null || maxCells <= 0 || !isAbyss(cell))
            {
                return 0;
            }
            for (int d = 1; d <= maxCells; d++)
            {
                for (int dx = -d; dx <= d; dx++)
                {
                    for (int dz = -d; dz <= d; dz++)
                    {
                        if (Mathf.Abs(dx) != d && Mathf.Abs(dz) != d)
                        {
                            continue; // 只掃第 d 圈
                        }
                        if (!isAbyss(new IntVec3(cell.x + dx, 0, cell.z + dz)))
                        {
                            return d;
                        }
                    }
                }
            }
            return maxCells;
        }

        /// <summary>深淵生成的雜訊門檻（0–1）：超過就挖成深淵（二期 P2-10）。</summary>
        public const float AbyssCarveThreshold = 0.72f;

        /// <summary>這個層級的地圖要不要產生深淵：只有深海與海床。淺海／變溫層要看得到海床。</summary>
        public static bool CarvesAbyss(DepthTier tier)
        {
            return tier == DepthTier.Deep || tier == DepthTier.Seabed;
        }

        /// <summary>某一格要不要挖成深淵：層級要對，而且雜訊要過門檻。純函式，可單元測試。</summary>
        public static bool ShouldCarveAbyss(DepthTier tier, float noise)
        {
            return CarvesAbyss(tier) && noise >= AbyssCarveThreshold;
        }

        /// <summary>
        /// 頂點色 r 的值：緊鄰崖壁（distance 1）最亮 1.0，之後線性變淡，**第 <paramref name="maxCells"/> 圈就是 0**，
        /// 更遠也是 0。
        ///
        /// <para>這裡刻意讓 d == maxCells 剛好是 0（而不是「還有 0.25」）：<see cref="AbyssRimDistance"/>
        /// 在找不到崖壁時會回上限，若上限那圈還有亮度，一大片深淵的內部就會浮現一層 0.25 的灰底，
        /// 永遠不會全黑——二期 P2-10 的完成指標要求「4 格以外是全黑」。</para>
        /// </summary>
        public static float AbyssRimWeight(int distance, int maxCells)
        {
            if (distance <= 0 || maxCells <= 0)
            {
                return 0f;
            }
            if (maxCells == 1)
            {
                return distance == 1 ? 1f : 0f;
            }
            return Mathf.Clamp01((maxCells - distance) / (float)(maxCells - 1));
        }

        /// <summary>
        /// 頂點周圍格子的光照平均。呼叫端只把有效的格子放進陣列（界外不計入；
        /// 迷霧格放 0 但仍然計入），<paramref name="count"/> 是有效格數。
        /// </summary>
        public static float AverageLight(float[] lights, int count)
        {
            int n = Mathf.Min(count, lights?.Length ?? 0);
            if (n <= 0)
            {
                return 0f;
            }
            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                sum += lights[i];
            }
            return sum / n;
        }

        /// <summary>
        /// 混濁度：光越亮越透明。和 <c>DepthRim_WaterOuter.frag.glsl</c>、<c>DepthRim_WaterVolume.frag.glsl</c>
        /// 的 <c>clarityOf</c> 是同一條公式，單元測試對照著色器。
        /// </summary>
        public static float WaterAlpha(float light, float clearAlpha, float murkAlpha, float lightMin, float lightMax)
        {
            float span = Mathf.Max(lightMax - lightMin, 0.0001f);
            float x = Mathf.Clamp01((light - lightMin) / span);
            float clarity = x * x * (3f - 2f * x);
            return Mathf.Lerp(murkAlpha, clearAlpha, clarity);
        }

        /// <summary>
        /// 暗處己方 Pawn 提示的不透明度：混濁度在門檻以下為 0，越混濁越明顯，上限 <paramref name="maxAlpha"/>。
        /// </summary>
        public static float HintAlpha(float turbidity, float threshold, float maxAlpha)
        {
            if (threshold >= 1f)
            {
                return 0f;
            }
            float x = Mathf.Clamp01((turbidity - threshold) / (1f - threshold));
            return x * maxAlpha;
        }

        // ── 水中聲音（二期 P2-11）─────────────────────────────────────────────

        /// <summary>悶音強度：聲源與聽者（鏡頭）任一方在水中就是悶的；兩邊都在乾的艙室才清楚。</summary>
        public static float AudioMuffle(float sourceWetness, float listenerWetness)
        {
            return Mathf.Clamp01(Mathf.Max(sourceWetness, listenerWetness));
        }

        /// <summary>
        /// 低通濾波的截止頻率（Hz）。比照 Odyssey 真空的曲線形狀（`SampleOneShot` 的 VacuumFrequencyCutoffCurve），
        /// 但水會傳聲，所以端點比真空高：0 → 22000、0.25 → 3000、1 → 700；深海與海床再降 20%。
        /// </summary>
        public static float MuffleCutoffHz(float muffle, DepthTier tier)
        {
            float m = Mathf.Clamp01(muffle);
            float hz = m <= 0.25f
                ? Mathf.Lerp(22000f, 3000f, m / 0.25f)
                : Mathf.Lerp(3000f, 700f, (m - 0.25f) / 0.75f);
            if (tier >= DepthTier.Deep && m > 0f)
            {
                hz *= 0.8f;
            }
            return hz;
        }

        /// <summary>音量倍率：0 → 1、0.25 → 0.85、1 → 0.55（真空是 0.05；水中只是變小聲）。</summary>
        public static float MuffleVolume(float muffle)
        {
            float m = Mathf.Clamp01(muffle);
            return m <= 0.25f ? Mathf.Lerp(1f, 0.85f, m / 0.25f) : Mathf.Lerp(0.85f, 0.55f, (m - 0.25f) / 0.75f);
        }

        /// <summary>
        /// 水密門的通行規則：門的任一側水位 ≥ 站立淹沒門檻時，只有「徵召中」「能在水中呼吸」「本來就泡在水裡」的角色能通過。
        /// 目的只是讓閒逛、工作中的殖民者不會自己走進水裡溺死；玩家下的命令、已經在水裡想回艙的人都不擋。
        /// </summary>
        public static bool WatertightDoorAllows(bool drafted, bool canBreatheUnderwater, bool pawnInWater, float maxSideLevel)
        {
            if (maxSideLevel < SubmergedLevelStanding)
            {
                return true;
            }
            return drafted || canBreatheUnderwater || pawnInWater;
        }

        /// <summary>站著的人被淹沒的水位（和 <see cref="FloodingPawnEffects.SubmergedLevelStanding"/> 相同）。</summary>
        public const float SubmergedLevelStanding = 0.75f;

        /// <summary>每次水位更新（250 ticks）船殼承壓聲的機率：淺海與變溫層 0；深海 4%、海床 8%；每面受損牆再加 2%，上限 30%。</summary>
        public static float HullCreakChance(DepthTier tier, int damagedWalls)
        {
            float baseChance = tier == DepthTier.Seabed ? 0.08f : tier == DepthTier.Deep ? 0.04f : 0f;
            float damage = 0.02f * Mathf.Max(0, damagedWalls);
            if (baseChance <= 0f && damagedWalls <= 0)
            {
                return 0f;
            }
            return Mathf.Min(0.3f, baseChance + damage);
        }

        // ── Pawn 水位渲染 ────────────────────────────────────────────────────

        /// <summary>遮罩階數（水線量化）。</summary>
        public const int CutSteps = 32;

        // 人形各節點在全身高度中的範圍，以及貼圖內實際有內容的 UV 高度範圍
        public const float BodyBandMax = 0.72f;
        public const float BodyUvMin = 0.12f;
        public const float BodyUvMax = 0.85f;
        public const float HeadBandMin = 0.62f;
        public const float HeadBandMax = 1f;
        public const float HeadUvMin = 0.2f;
        public const float HeadUvMax = 0.85f;

        public const float RenderMinLevel = 0.02f;

        /// <summary>
        /// 節點的水線階數（0 = 不在水中，<see cref="CutSteps"/> = 整個節點在水面下）。
        /// 人形身體約佔全身高度 0–<see cref="BodyBandMax"/>、頭部約佔 <see cref="HeadBandMin"/>–1；
        /// 非人形整隻視為身體；躺著時水位到 <paramref name="lyingSubmergedLevel"/> 就整個上色。
        /// </summary>
        public static int CutStep(float level, bool humanlike, bool headNode, bool standing, float lyingSubmergedLevel)
        {
            if (level <= RenderMinLevel)
            {
                return 0;
            }
            if (!standing)
            {
                return level >= lyingSubmergedLevel ? CutSteps : 0;
            }
            float cut;
            if (!humanlike)
            {
                cut = Mathf.Lerp(BodyUvMin, BodyUvMax, level);
            }
            else if (headNode)
            {
                if (level <= HeadBandMin)
                {
                    return 0;
                }
                cut = Mathf.Lerp(HeadUvMin, HeadUvMax, Mathf.InverseLerp(HeadBandMin, HeadBandMax, level));
            }
            else
            {
                cut = level >= BodyBandMax ? 1f : Mathf.Lerp(BodyUvMin, BodyUvMax, Mathf.InverseLerp(0f, BodyBandMax, level));
            }
            return Mathf.Clamp(Mathf.RoundToInt(cut * CutSteps), 0, CutSteps);
        }
    }
}
