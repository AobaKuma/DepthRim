using DepthRim;
// 只為了 IntVec3（純值型別）；這個檔案的其他部分仍然只測純函式。
using Verse;

// 水位系統的公式（FloodingMath）與潛水氧氣（CompDivingAir）。
static partial class Test
{
    const float UpdateSeconds = MapComponent_Flooding.UpdateInterval / 60f;

    static void FloodingLeak()
    {
        Check(FloodingMath.LeakVolume(100, 100, DepthTier.Shallow, 1f, 0f, UpdateSeconds) == 0f, "undamaged wall does not leak");
        Check(FloodingMath.LeakVolume(0, 0, DepthTier.Shallow, 1f, 0f, UpdateSeconds) == 0f, "wall without hit points does not leak");
        // 0.6 格/秒 × 受損 50% × 水壓 1 × 水位差 1 × 4.1667 秒
        Near(FloodingMath.LeakVolume(50, 100, DepthTier.Shallow, 1f, 0f, UpdateSeconds), 0.6f * 0.5f * UpdateSeconds, "half-damaged wall, shallows, full head");
        float shallow = FloodingMath.LeakVolume(50, 100, DepthTier.Shallow, 1f, 0f, UpdateSeconds);
        Near(FloodingMath.LeakVolume(50, 100, DepthTier.Seabed, 1f, 0f, UpdateSeconds), shallow * 8f, "seabed leaks 8x the shallows");
        Near(FloodingMath.LeakVolume(50, 100, DepthTier.Shallow, 1f, 0.5f, UpdateSeconds), shallow * 0.5f, "leak scales with the level difference");
        Check(FloodingMath.LeakVolume(50, 100, DepthTier.Deep, 0.4f, 0.6f, UpdateSeconds) == 0f, "no leak towards the higher side");
        Check(FloodingMath.LeakVolume(50, 100, DepthTier.Deep, 0.5f, 0.5f, UpdateSeconds) == 0f, "no leak at equal levels");
        Check(FloodingMath.LeakVolume(1, 100, DepthTier.Shallow, 1f, 0f, UpdateSeconds) > FloodingMath.LeakVolume(99, 100, DepthTier.Shallow, 1f, 0f, UpdateSeconds), "more damage leaks more");
    }

    static void FloodingDoorFlow()
    {
        Check(FloodingMath.DoorFlowVolume(0.5f, 0.5f, 50f, 50f, UpdateSeconds) == 0f, "no flow at equal levels");
        Check(FloodingMath.DoorFlowVolume(0.501f, 0.5f, 50f, 50f, UpdateSeconds) == 0f, "no flow below epsilon");
        // 大房間：受速率限制
        Near(FloodingMath.DoorFlowVolume(1f, 0f, 10000f, 10000f, UpdateSeconds), FloodingMath.DoorFlowCellsPerSecond * UpdateSeconds, "rate-limited between large rooms");
        // 小房間：上限是把較小房間拉到平均值
        Near(FloodingMath.DoorFlowVolume(1f, 0f, float.MaxValue, 4f, UpdateSeconds), 2f, "capped by the smaller room (open water on the other side)");
        float a = FloodingMath.DoorFlowVolume(1f, 0f, 20f, 20f, UpdateSeconds);
        Check(FloodingMath.ApplyVolume(0f, a, 20) <= FloodingMath.ApplyVolume(1f, -a, 20) + 1e-4f, "one update never overshoots the average");
    }

    static void FloodingVolume()
    {
        Near(FloodingMath.ApplyVolume(0.5f, 10f, 100), 0.6f, "10 cells of water into a 100-cell room");
        Check(FloodingMath.ApplyVolume(0.9f, 50f, 10) == 1f, "clamped at full");
        Check(FloodingMath.ApplyVolume(0.1f, -50f, 10) == 0f, "clamped at dry");
        Near(FloodingMath.ApplyVolume(0f, 1f, 0), 1f, "zero-cell room treated as one cell");
        for (int i = 0; i <= 255; i += 15)
        {
            byte b = (byte)i;
            Check(FloodingMath.ToByte(FloodingMath.FromByte(b)) == b, $"byte round trip {i}");
        }
        Check(FloodingMath.ToByte(-1f) == 0 && FloodingMath.ToByte(2f) == 255, "byte conversion clamps");
    }

    /// <summary>設計稿的實際流程：10×10 艙室、一台排水泵，從滿水抽乾。</summary>
    static void FloodingPumpDrainTime()
    {
        const int cells = 100;
        const float pumpCellsPerSecond = 2f; // DepthRim_BilgePump
        float level = 1f;
        int updates = 0;
        while (level > 0f && updates < 1000)
        {
            level = FloodingMath.FromByte(FloodingMath.ToByte(FloodingMath.ApplyVolume(level, -pumpCellsPerSecond * UpdateSeconds, cells)));
            updates++;
        }
        int ticks = updates * MapComponent_Flooding.UpdateInterval;
        Check(level == 0f, "room drains completely");
        Check(ticks >= 2500 && ticks <= 3500, $"10x10 room drains in about 1.2 game hours ({ticks} ticks)");

        // 受損牆在海床的滲水量超過一台泵：玩家必須修牆，不能只靠抽水
        float leak = FloodingMath.LeakVolume(10, 100, DepthTier.Seabed, 1f, 0f, UpdateSeconds);
        Check(leak > pumpCellsPerSecond * UpdateSeconds, "a badly damaged wall on the seabed outpaces one pump");
    }

    static void Pressure()
    {
        Check(FloodingMath.PressureFactor(DepthTier.Shallow) == 1f, "shallows x1");
        Check(FloodingMath.PressureFactor(DepthTier.Thermocline) == 2f, "thermocline x2");
        Check(FloodingMath.PressureFactor(DepthTier.Deep) == 4f, "deep x4");
        Check(FloodingMath.PressureFactor(DepthTier.Seabed) == 8f, "seabed x8");
        for (DepthTier t = DepthTier.Shallow; t <= DepthTier.Seabed; t++)
        {
            Check(FloodingMath.RequiredPressureResistance(t) == (int)t, $"{t} needs resistance {(int)t}");
        }
        Check(FloodingMath.RequiredPressureResistance(DepthTier.Shallow) == 0, "the ice ocean base needs no pressure gear");
    }

    static void WaterRenderCut()
    {
        const int full = FloodingMath.CutSteps;
        const float lying = FloodingPawnEffects.SubmergedLevelLying;
        Check(FloodingMath.CutStep(0f, true, false, true, lying) == 0, "dry: no cut");
        Check(FloodingMath.CutStep(FloodingMath.RenderMinLevel, true, false, true, lying) == 0, "puddle below render threshold");
        Check(FloodingMath.CutStep(FloodingMath.BodyBandMax, true, false, true, lying) == full, "body fully under at the body band top");
        Check(FloodingMath.CutStep(1f, true, false, true, lying) == full, "body fully under at full water");
        Check(FloodingMath.CutStep(0.5f, true, true, true, lying) == 0, "head dry at waist depth");
        Check(FloodingMath.CutStep(FloodingMath.HeadBandMin, true, true, true, lying) == 0, "head dry right at the head band bottom");
        Check(FloodingMath.CutStep(0.9f, true, true, true, lying) > 0, "head partly under at 90%");
        Check(FloodingMath.CutStep(lying - 0.01f, true, false, false, lying) == 0, "lying just below threshold: no tint");
        Check(FloodingMath.CutStep(lying, true, true, false, lying) == full, "lying at threshold: fully tinted (head too)");

        int prevBody = -1, prevHead = -1, prevAnimal = -1;
        bool monotonic = true;
        for (float level = 0f; level <= 1.0001f; level += 0.02f)
        {
            int body = FloodingMath.CutStep(level, true, false, true, lying);
            int head = FloodingMath.CutStep(level, true, true, true, lying);
            int animal = FloodingMath.CutStep(level, false, false, true, lying);
            monotonic &= body >= prevBody && head >= prevHead && animal >= prevAnimal;
            monotonic &= body >= 0 && body <= full && head >= 0 && head <= full && animal >= 0 && animal <= full;
            prevBody = body; prevHead = head; prevAnimal = animal;
        }
        Check(monotonic, "cut steps rise monotonically with the water level and stay in range");
        Check(FloodingMath.CutStep(0.36f, true, false, true, lying) > FloodingMath.CutStep(0.36f, false, false, true, lying), "humanlike body is shorter than a whole animal");
    }

    /// <summary>水體網格頂點：只平均同一片水，岸邊程度 = 實心格比例。</summary>
    static void WaterCorners()
    {
        var S = FloodingMath.CornerCell.SameWater;
        var W = FloodingMath.CornerCell.Solid;
        var X = FloodingMath.CornerCell.Ignore;
        float level, shore;

        FloodingMath.WaterCorner(new[] { S, S, S, S }, new[] { 1f, 1f, 0.5f, 0.5f }, out level, out shore);
        Near(level, 0.75f, "open interior averages all four cells");
        Near(shore, 0f, "no shore in open interior");

        FloodingMath.WaterCorner(new[] { W, W, S, S }, new[] { 0f, 0f, 1f, 1f }, out level, out shore);
        Near(level, 1f, "wall cells do not dilute the level");
        Near(shore, 0.5f, "corner along a wall is half shore");

        FloodingMath.WaterCorner(new[] { W, W, W, S }, new[] { 0f, 0f, 0f, 0.4f }, out level, out shore);
        Near(level, 0.4f, "inner corner keeps the cell's own level");
        Near(shore, 0.75f, "inner corner is mostly shore");

        // 乾艙室的格子與隔壁淹水的房間只隔一道斜角：另一間房間不參與平均，乾艙室頂點保持 0
        FloodingMath.WaterCorner(new[] { X, W, W, S }, new[] { 1f, 0f, 0f, 0f }, out level, out shore);
        Near(level, 0f, "water from another room never bleeds into a dry compartment");

        FloodingMath.WaterCorner(new[] { X, X, X, S }, new[] { 0f, 0f, 0f, 0.8f }, out level, out shore);
        Near(level, 0.8f, "map edge corner keeps the cell's level");
        Near(shore, 0f, "map edge is not shore");
    }

    /// <summary>外海層（迷霧之上）涵蓋哪些格子。</summary>
    static void OuterCoverage()
    {
        Check(FloodingMath.OuterIncludes(true, false, false, false), "fogged compartment cell is covered (no info leak)");
        Check(FloodingMath.OuterIncludes(true, true, false, true), "fogged wall next to a compartment is covered (no info leak)");
        Check(FloodingMath.OuterIncludes(false, false, true, false), "explored open water is covered");
        Check(!FloodingMath.OuterIncludes(false, false, false, false), "explored compartment interior is cut out");
        Check(FloodingMath.OuterIncludes(false, true, false, false), "explored rock in open water is covered");
        Check(!FloodingMath.OuterIncludes(false, true, false, true), "explored compartment wall is cut out");
    }

    /// <summary>水體視覺（實作指南 A.4、B.6、B2.4）：波紋週期、光照換算、混濁度與提示 alpha。</summary>
    static void FloodingVisual()
    {
        // A.4：波紋重複週期 = 1 / 尺度
        Near(FloodingMath.RipplePeriodCells(0.03f), 33.333f, "ripple period of the outer layer (0.03)", 0.01f);
        Near(FloodingMath.RipplePeriodCells(0.06f), 16.667f, "ripple period of a compartment (0.06)", 0.01f);
        Check(FloodingMath.RipplePeriodCells(0f) == 0f, "no ripple scale: no period");
        Check(FloodingMath.RipplePeriodCells(-1f) == 0f, "negative scale treated as none");
        Check(FloodingMath.RipplePeriodCells(0.03f) >= 25f, "A.3: outer ripples repeat at least every 25 cells");
        Check(FloodingMath.RipplePeriodCells(0.06f) >= 12f, "A.3: compartment ripples repeat at least every 12 cells");

        // B.1：光照 = 最亮通道
        Near(FloodingMath.LightOf(new UnityEngine.Color32(0, 0, 0, 255)), 0f, "no glow: no light");
        Near(FloodingMath.LightOf(new UnityEngine.Color32(255, 0, 0, 255)), 1f, "red glow is full light");
        Near(FloodingMath.LightOf(new UnityEngine.Color32(0, 128, 0, 255)), 128f / 255f, "green channel picked when it is the brightest");
        float[] lights = { 0.2f, 0.4f, 0.6f, 0f };
        Near(FloodingMath.AverageLight(lights, 4), 0.3f, "average of four corners (a fogged corner counts as 0)");
        Near(FloodingMath.AverageLight(lights, 3), 0.4f, "out-of-bounds corners are not counted");
        Near(FloodingMath.AverageLight(lights, 0), 0f, "no valid corner: 0");

        // B.2：混濁度（和兩個著色器的 clarityOf 同一條公式）
        const float lightMin = 0.05f;
        const float lightMax = 0.6f;
        Near(FloodingMath.WaterAlpha(0f, 0.15f, 0.85f, lightMin, lightMax), 0.85f, "no light: fully murky");
        Near(FloodingMath.WaterAlpha(1f, 0.15f, 0.85f, lightMin, lightMax), 0.15f, "bright light: fully clear");
        Near(FloodingMath.WaterAlpha(lightMin, 0.15f, 0.85f, lightMin, lightMax), 0.85f, "at the lower bound: still murky");
        Near(FloodingMath.WaterAlpha(lightMax, 0.15f, 0.85f, lightMin, lightMax), 0.15f, "at the upper bound: clear");
        float prev = 1f;
        bool monotonic = true;
        for (float light = 0f; light <= 1.0001f; light += 0.05f)
        {
            float alpha = FloodingMath.WaterAlpha(light, 0.15f, 0.85f, lightMin, lightMax);
            monotonic &= alpha <= prev + 1e-5f && alpha >= 0.15f - 1e-5f && alpha <= 0.85f + 1e-5f;
            prev = alpha;
        }
        Check(monotonic, "more light never makes the water murkier and stays within the two bounds");
        Near(FloodingMath.WaterAlpha(0.5f, 0.2f, 0.2f, lightMin, lightMax), 0.2f, "equal bounds stay constant");
        Check(!float.IsNaN(FloodingMath.WaterAlpha(0.5f, 0.1f, 0.9f, 0.4f, 0.4f)), "lightMin == lightMax does not divide by zero");
        Check(FloodingMath.WaterAlpha(-5f, 0.1f, 0.9f, lightMin, lightMax) == 0.9f, "negative light clamps to murky");

        // B2.4：提示 alpha（門檻以下為 0、單調遞增、上限）
        const float threshold = MapComponent_DarkWaterHints.Threshold;
        const float maxAlpha = MapComponent_DarkWaterHints.MaxAlpha;
        Check(FloodingMath.HintAlpha(0f, threshold, maxAlpha) == 0f, "clear water: no hint");
        Check(FloodingMath.HintAlpha(threshold, threshold, maxAlpha) == 0f, "exactly at the threshold: no hint");
        Check(FloodingMath.HintAlpha(0.4f, threshold, maxAlpha) == 0f, "below the threshold: no hint");
        Near(FloodingMath.HintAlpha(1f, threshold, maxAlpha), maxAlpha, "fully murky: maximum hint");
        Check(FloodingMath.HintAlpha(2f, threshold, maxAlpha) == maxAlpha, "clamped at the maximum");
        Near(FloodingMath.HintAlpha(0.75f, threshold, maxAlpha), 0.5f * maxAlpha, "halfway past the threshold: half the maximum");
        float prevHint = 0f;
        bool hintMonotonic = true;
        for (float turbidity = 0f; turbidity <= 1.0001f; turbidity += 0.05f)
        {
            float alpha = FloodingMath.HintAlpha(turbidity, threshold, maxAlpha);
            hintMonotonic &= alpha >= prevHint - 1e-5f && alpha >= 0f && alpha <= maxAlpha + 1e-5f;
            prevHint = alpha;
        }
        Check(hintMonotonic, "hint alpha rises monotonically and never exceeds the maximum");
        Check(FloodingMath.HintAlpha(1f, 1f, maxAlpha) == 0f, "threshold of 1 never shows a hint");
        Check(MapComponent_DarkWaterHints.HintColor.a == 1f, "hint colour is opaque; alpha comes from the tint");

        // 顏色是 B2.2 的 (120, 220, 255)
        Near(MapComponent_DarkWaterHints.HintColor.r * 255f, 120f, "hint colour red", 0.5f);
        Near(MapComponent_DarkWaterHints.HintColor.g * 255f, 220f, "hint colour green", 0.5f);
        Near(MapComponent_DarkWaterHints.HintColor.b * 255f, 255f, "hint colour blue", 0.5f);

        // D9：光照 = 日光與人造光取大者（日光取原版 GroundGlowAt 的定義，人造光用原始平滑值）
        UnityEngine.Color32 dark = new UnityEngine.Color32(0, 0, 0, 0);
        UnityEngine.Color32 lampMid = new UnityEngine.Color32(90, 62, 40, 4);      // 燈下 3 格左右：90/255
        UnityEngine.Color32 lampCell = new UnityEngine.Color32(203, 141, 89, 1);   // 燈的正下方：203/255
        Check(FloodingMath.CombineLight(0f, 1f, dark, roofed: false) == 0f, "no sun, no lamp: no light");
        Near(FloodingMath.CombineLight(1f, 1f, dark, roofed: false), 1f, "full sun: fully lit");
        Near(FloodingMath.CombineLight(0.7f, 1f, dark, roofed: false), 0.7f, "shallow water counts daylight as light");
        Near(FloodingMath.CombineLight(0.7f, 0.25f, dark, roofed: false), 0.175f, "deep water only gets a quarter of the daylight");
        Check(FloodingMath.CombineLight(1f, 0f, dark, roofed: false) == 0f, "a tier with no daylight reach stays dark in the sun");
        Check(FloodingMath.CombineLight(1f, 1f, dark, roofed: true) == 0f, "a roof blocks the daylight");
        Near(FloodingMath.CombineLight(0f, 0.25f, lampCell, roofed: false), 203f / 255f, "the lamp keeps its smooth falloff under water");
        Check(FloodingMath.CombineLight(0.1f, 1f, lampCell, roofed: false) > 0.7f, "a lamp outshines a dim sky");
        Near(FloodingMath.CombineLight(1f, 1f, lampCell, roofed: false), 1f, "in full daylight the daylight wins");
        Near(FloodingMath.CombineLight(0f, 1f, lampMid, roofed: true), 90f / 255f, "artificial light is not scaled or capped", 0.002f);
        Near(FloodingMath.CombineLight(0.2f, 1f, lampMid, roofed: false), 90f / 255f, "the brighter of the two wins", 0.002f);
        Near(FloodingMath.CombineLight(0.9f, 1f, lampMid, roofed: false), 0.9f, "daylight wins when it is brighter", 0.002f);

        // P2-10：深淵離崖距離（純函式，用字串地圖當深淵判斷）
        //  "....."
        //  ".###."
        //  ".###."   '#' = 深淵，'.' = 實心地形
        //  ".###."
        //  "....."
        string[] rows = { ".....", ".###.", ".###.", ".###.", "....." };
        System.Func<IntVec3, bool> isAbyss = c =>
            c.x >= 0 && c.z >= 0 && c.z < rows.Length && c.x < rows[c.z].Length && rows[c.z][c.x] == '#';
        Check(FloodingMath.AbyssRimDistance(new IntVec3(0, 0, 0), 4, isAbyss) == 0, "abyss: solid ground has distance 0");
        Check(FloodingMath.AbyssRimDistance(new IntVec3(1, 0, 1), 4, isAbyss) == 1, "abyss: the corner of the hole is 1");
        Check(FloodingMath.AbyssRimDistance(new IntVec3(2, 0, 2), 4, isAbyss) == 2, "abyss: the middle of a 3x3 hole is 2");
        Check(FloodingMath.AbyssRimDistance(new IntVec3(9, 0, 9), 4, isAbyss) == 0, "abyss: cells outside the map are not abyss");
        Check(FloodingMath.AbyssRimDistance(new IntVec3(2, 0, 2), 4, c => true) == 4, "abyss: no rim within the cap returns the cap");
        Check(FloodingMath.AbyssRimDistance(new IntVec3(2, 0, 2), 0, isAbyss) == 0, "abyss: a zero cap is safe");
        Check(FloodingMath.AbyssRimDistance(IntVec3.Zero, 4, null) == 0, "abyss: a null predicate is safe");
        // r 通道的權重：緊鄰最亮，第 maxCells 圈就是 0（大面積深淵的內部才會真的全黑）
        Near(FloodingMath.AbyssRimWeight(1, 4), 1f, "abyss: the rim itself is fully bright");
        Near(FloodingMath.AbyssRimWeight(2, 4), 2f / 3f, "abyss: the second ring fades");
        Near(FloodingMath.AbyssRimWeight(3, 4), 1f / 3f, "abyss: the third ring is fainter");
        Check(FloodingMath.AbyssRimWeight(4, 4) == 0f, "abyss: the outermost ring is already fully dark");
        Check(FloodingMath.AbyssRimWeight(5, 4) == 0f, "abyss: past the cap stays fully dark");
        Check(FloodingMath.AbyssRimWeight(0, 4) == 0f, "abyss: solid ground carries no rim weight");
        // 這一條是 V9 的前提：11x11 的正中央（離崖 5 格）必須是 0，否則深淵內部永遠有一層灰底
        Check(FloodingMath.AbyssRimWeight(FloodingMath.AbyssRimDistance(new IntVec3(2, 0, 2), 4, c => true), 4) == 0f,
            "abyss: the deep interior of a large hole carries no rim weight");
        Check(FloodingMath.AbyssRimWeight(1, 1) == 1f && FloodingMath.AbyssRimWeight(2, 1) == 0f, "abyss: a single-cell rim range is safe");
        // 「深淵吸光」是深淵自己的常數，不是借用層級的 ambientReach：必須很小但不能是 0
        // （0 會讓崖邊也全黑，什麼都看不到）。計畫 §2.7.3。
        Check(SectionLayer_AbyssBackdrop.AmbientReach > 0f && SectionLayer_AbyssBackdrop.AmbientReach <= 0.1f,
            $"abyss: the backdrop swallows daylight (ambient reach {SectionLayer_AbyssBackdrop.AmbientReach})");
        Check(SectionLayer_AbyssBackdrop.RimCells >= 2 && SectionLayer_AbyssBackdrop.RimCells <= 8,
            $"abyss: the rim range is sane ({SectionLayer_AbyssBackdrop.RimCells} cells)");
        // 深淵生成（P2-10）：只有深海／海床要挖，而且雜訊要過門檻
        Check(FloodingMath.CarvesAbyss(DepthTier.Deep) && FloodingMath.CarvesAbyss(DepthTier.Seabed),
            "abyss gen: the deep sea and the seabed get abyss holes");
        Check(!FloodingMath.CarvesAbyss(DepthTier.Shallow) && !FloodingMath.CarvesAbyss(DepthTier.Thermocline),
            "abyss gen: the shallows and thermocline keep a visible floor");
        Check(FloodingMath.ShouldCarveAbyss(DepthTier.Seabed, 0.9f), "abyss gen: high noise carves on the seabed");
        Check(!FloodingMath.ShouldCarveAbyss(DepthTier.Seabed, 0.5f), "abyss gen: low noise leaves the floor alone");
        Check(!FloodingMath.ShouldCarveAbyss(DepthTier.Shallow, 0.99f), "abyss gen: even high noise never carves the shallows");
        Check(FloodingMath.AbyssCarveThreshold > 0.5f && FloodingMath.AbyssCarveThreshold < 0.95f,
            $"abyss gen: the carve threshold is sane ({FloodingMath.AbyssCarveThreshold})");
        // P2-5：玩家設定真的會改變材質參數（不只是「編譯得過」）。
        // 這裡驗的是「設定 → 材質值」這一段純計算；設定視窗本身的外觀仍然沒有人驗過。
        DepthRimModSettings settings = new DepthRimModSettings();
        DepthRimModSettings.SetCurrent(settings);
        WaterVisualTierDef tierDef = new WaterVisualTierDef { murkAlpha = 0.8f };
        Near(WaterTierMaterials.MurkFor(tierDef), 0.8f, "P2-5: the neutral default keeps the Def murk value");
        settings.murkScale = 0.5f;
        Near(WaterTierMaterials.MurkFor(tierDef), 0.4f, "P2-5: the murk scale multiplies the material value");
        settings.particles = false;
        Check(WaterTierMaterials.ParticleFor(1f) == 0f, "P2-5: turning particles off zeroes the shader parameter");
        settings.particles = true;
        Near(WaterTierMaterials.ParticleFor(0.7f), 0.7f, "P2-5: particles on keeps the Def value");
        DepthRimModSettings.SetCurrent(null);
        Near(WaterTierMaterials.MurkFor(tierDef), 0.8f, "P2-5: a missing settings instance falls back to neutral defaults");
        // 層級來源（P2-4）用的是既有的 FloodingUtility.DepthTierOf → map.Parent is IDepthTierProvider；
        // 那需要真的 Map 與 MapParent，單元測試建不出來，所以由文件與 V6（除錯覆寫優先序）涵蓋。
    }

    static void DivingAir()
    {        CompDivingAir air = new CompDivingAir { props = new CompProperties_DivingAir { capacityTicks = 1000, refillPerTick = 10f } };
        Near(air.Air, 1000f, "starts full");
        Near(air.AirPercent, 1f, "100% when full");
        for (int i = 0; i < 4; i++) { Check(air.TryConsume(250), $"consume #{i + 1}"); }
        Near(air.Air, 0f, "empty after 1000 ticks underwater");
        Check(!air.TryConsume(250), "cannot breathe from an empty tank");
        air.Refill(25);
        Near(air.Air, 250f, "refills 10 ticks of air per tick out of water");
        air.Refill(1000);
        Near(air.Air, 1000f, "refill clamps at capacity");
        air.TryConsume(1500);
        Near(air.Air, 0f, "consumption clamps at zero");

        CompProperties_DivingAir shipped = new CompProperties_DivingAir();
        Check(shipped.capacityTicks == 30000, "default tank lasts 12 game hours");
        Check(shipped.capacityTicks / shipped.refillPerTick <= 3000, "default tank refills within ~1.2 game hours");
    }
}

// 露天房間慢慢進水、進水效果依牆體耐久分階段（使用者回報，2026-11-01）。
static partial class Test
{
    static void OpenFillAndLeakStages()
    {
        float s = UpdateSeconds;
        Check(FloodingMath.OpenRoomFill(true, 0, 0, DepthTier.Shallow, s) == 0f, "nothing left to fill: no rise");
        float sea = FloodingMath.OpenRoomFill(true, 0, 100, DepthTier.Shallow, s);
        Near(sea, FloodingMath.SeaFillPerSecond * s, "sea-connected cells rise at the base rate in the shallows");
        Check(sea > 0f && sea < 1f, $"the shallows do not fill in a single update ({sea:0.00})");
        Check(FloodingMath.OpenRoomFill(true, 0, 100, DepthTier.Seabed, s) > sea, "deeper water pours in faster");
        float roof1 = FloodingMath.OpenRoomFill(false, 1, 100, DepthTier.Shallow, s);
        float roof5 = FloodingMath.OpenRoomFill(false, 5, 100, DepthTier.Shallow, s);
        Check(roof1 > 0f && roof5 > roof1, "more roof holes pour in more water");
        Check(roof1 < sea, "a single roof hole fills slower than an open breach");
        Check(FloodingMath.OpenRoomFill(false, 1000, 10, DepthTier.Shallow, s) <= sea + 1e-6f, "roof inflow never exceeds the open-sea rate");
        Check(FloodingMath.OpenRoomFill(false, 0, 100, DepthTier.Shallow, s) == 0f, "no roof hole and no edge: no inflow");

        Check(FloodingMath.LeakStage(0.9f) == 0 && FloodingMath.LeakStage(0.6f) == 1 && FloodingMath.LeakStage(0.4f) == 2 && FloodingMath.LeakStage(0.1f) == 3,
            "leak stages follow wall durability (>75 / 50–75 / 25–50 / <25 %)");
        Check(FloodingMath.LeakStage(-1f) == 3 && FloodingMath.LeakStage(2f) == 0, "leak stage clamps out-of-range durability");
        bool grows = true;
        for (int st = 1; st <= 3; st++)
        {
            FloodingMath.LeakEffect(st - 1, out int c0, out float sc0, out float sp0);
            FloodingMath.LeakEffect(st, out int c1, out float sc1, out float sp1);
            grows &= c1 > c0 && sc1 > sc0 && sp1 > sp0;
            FloodingAudio.FloodLoopParams(st - 1, out float v0, out float p0);
            FloodingAudio.FloodLoopParams(st, out float v1, out float p1);
            grows &= v1 > v0 && p1 < p0;
        }
        Check(grows, "each stage sprays more, bigger and faster, and sounds louder and lower");

        Check(FloodingMath.BuildingSubmerged(0.3f, 0.5f), "a low building (0.3) is submerged at half flood");
        Check(!FloodingMath.BuildingSubmerged(0.9f, 0.5f), "a tall building (0.9) sticks out at half flood");
        Check(FloodingMath.BuildingSubmerged(0.9f, 1f), "everything is submerged in a full room");
        Check(!FloodingMath.BuildingSubmerged(0f, 0.05f), "a puddle does not cover flat buildings");
        Check(FloodingMath.BuildingSubmerged(0f, FloodingMath.MinSubmergeLevel), "flat buildings are covered from the minimum level");
    }
}
