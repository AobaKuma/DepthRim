using System.Collections.Generic;
using System.Linq;
using DepthRim;

// 破冰上浮的純計算（IceBreachMath）與冰層開裂圖樣（IceCrackPattern），設計稿 Docs/潛艇破冰出水_機制設計.md §3～§5、§7.2。
static partial class Test
{
    static void IceBreachMathTests()
    {
        // ── 冰厚：Lebedev 表（§3.2）──
        Near(IceBreachMath.LebedevThickness(500f), 0.49f, "FDD 500: 0.49 m", 0.01f);
        Near(IceBreachMath.LebedevThickness(1000f), 0.73f, "FDD 1000: 0.73 m", 0.01f);
        Near(IceBreachMath.LebedevThickness(2000f), 1.09f, "FDD 2000: 1.09 m", 0.01f);
        Near(IceBreachMath.LebedevThickness(4000f), 1.63f, "FDD 4000: 1.63 m", 0.01f);
        Near(IceBreachMath.LebedevThickness(6000f), 2.07f, "FDD 6000: 2.07 m", 0.01f);
        Near(IceBreachMath.LebedevThickness(0f), 0f, "no frost, no ice");

        // 一年冰：從最暖的十二分之一年之後累加；越冷越厚；夾在 0～3.5 m
        float[] polar = Enumerable.Repeat(-25f, 12).ToArray();
        polar[0] = 5f;
        Near(IceBreachMath.FirstYearThickness(polar, 0), 0f, "at the warmest twelfth the first-year ice is gone");
        float early = IceBreachMath.FirstYearThickness(polar, 3);
        float late = IceBreachMath.FirstYearThickness(polar, 11);
        Check(early > 0f && late > early, $"ice thickens through the winter ({early:0.00} → {late:0.00} m)");
        float[] colder = polar.Select(t => t < 0f ? t - 10f : t).ToArray();
        Check(IceBreachMath.FirstYearThickness(colder, 11) > late, "colder winters grow thicker ice");
        float[] thaw = (float[])polar.Clone();
        thaw[10] = 8f;
        Check(IceBreachMath.FirstYearThickness(thaw, 11) < late, "a thaw melts some ice");
        float[] extreme = Enumerable.Repeat(-200f, 12).ToArray();
        extreme[0] = -199f;
        Near(IceBreachMath.FirstYearThickness(extreme, 11), IceBreachMath.MaxIceThickness, "clamped at 3.5 m");
        float[] warm = Enumerable.Repeat(10f, 12).ToArray();
        Near(IceBreachMath.FirstYearThickness(warm, 6), 0f, "no ice where it never freezes");

        // 多年冰比一年冰厚 1～2 m（再乘格內雜訊 0.8～1.2）；冰間水道只剩 1/4（至少 0.1 m）；冰棚與沒有冰回傳 0
        Near(IceBreachMath.Thickness(IceKind.MultiYear, 0.5f, 0f, 0.5f, false), 1.5f, "multi-year ice: first-year + 1 m at mid noise");
        Near(IceBreachMath.Thickness(IceKind.MultiYear, 0.5f, 1f, 0.5f, false), 2.5f, "multi-year ice: first-year + 2 m at most");
        Near(IceBreachMath.Thickness(IceKind.FirstYear, 1f, 0f, 0f, false), 0.8f, "local noise down to x0.8");
        Near(IceBreachMath.Thickness(IceKind.FirstYear, 1f, 0f, 1f, false), 1.2f, "local noise up to x1.2");
        Near(IceBreachMath.Thickness(IceKind.FirstYear, 1f, 0f, 0.5f, true), 0.25f, "a lead leaves a quarter of the ice");
        Near(IceBreachMath.Thickness(IceKind.FirstYear, 0.2f, 0f, 0.5f, true), 0.1f, "a lead still has at least 0.1 m");
        Near(IceBreachMath.Thickness(IceKind.Shelf, 1f, 1f, 1f, false), 0f, "an ice shelf is not breakable ice");
        Near(IceBreachMath.Thickness(IceKind.None, 1f, 1f, 1f, false), 0f, "open sea has no ice");
        Check(IceBreachMath.HasLead(0.1f, false) && !IceBreachMath.HasLead(0.2f, false) && IceBreachMath.HasLead(0.3f, true), "leads: 15%, 40% at season change");

        // ── 強度與特徵長度（§3.4、§4.1）──
        Near(IceBreachMath.FlexuralStrength(-20f), 0.52f, "-20 °C: 0.52 MPa");
        Near(IceBreachMath.FlexuralStrength(5f), 0.40f, "above freezing: 0.40 MPa");
        Near(IceBreachMath.FlexuralStrength(-80f), 0.75f, "capped at 0.75 MPa");
        Check(IceBreachMath.FlexuralStrength(-30f) > IceBreachMath.FlexuralStrength(-10f), "colder ice is stronger");
        Near(IceBreachMath.CharacteristicLength(0.3f), 5.95f, "l_c(0.3 m) = 5.95 m", 0.05f);
        Near(IceBreachMath.CharacteristicLength(1.2f), 16.8f, "l_c(1.2 m) = 16.8 m", 0.1f);
        Near(IceBreachMath.CharacteristicLength(2.5f), 29.2f, "l_c(2.5 m) = 29.2 m", 0.15f);
        Near(IceBreachMath.CharacteristicLengthCells(1.2f), 8.4f, "l_c(1.2 m) = 8.4 cells", 0.05f);
        Near(IceBreachMath.BreakForce(0.5f, 2f) / IceBreachMath.BreakForce(0.5f, 1f), 4f, "P_break grows with h_eff squared");

        // ── 判定：設計稿 §4.6 的範例（24×8 船體、−20 °C、1.2 m）──
        IceBreachOutcome first = IceBreachMath.Evaluate(1.2f, -20f, 192, cracked: false, frameLimit: 2f, frames: 1);
        Near(first.breakForce / 1e6f, 2.25f, "example: P_break 2.25 MN", 0.01f);
        Near(first.liftLoad / 1e6f, 2.49f, "example: W_lift 2.49 MN", 0.01f);
        Near(first.buoyancy / 1e6f, 4.52f, "example: F_up 4.52 MN", 0.01f);
        Near(first.ratio, 0.95f, "example first try: R 0.95", 0.01f);
        Check(first.result == IceBreachResult.Partial, $"example first try cracks the ice ({first.result})");
        Near(first.damagePerFrame, 175f, "example first try: 175 damage", 1f);
        IceBreachOutcome second = IceBreachMath.Evaluate(1.2f, -20f, 192, cracked: true, frameLimit: 2f, frames: 1);
        Near(second.ratio, 1.26f, "example second try: R 1.26", 0.01f);
        Check(second.result == IceBreachResult.Surfaced, $"example second try surfaces ({second.result})");
        Near(second.damagePerFrame, 66f, "example second try: 66 damage", 1f);
        Near(second.liftLoad, first.liftLoad, "cracked ice weighs the same");

        // 四個 R 區間（調船體大小讓 R 落在邊界兩側）
        IceBreachResult ResultAt(float ratioWanted)
        {
            // R 隨船體格數單調上升：二分搜尋出 R 剛好跨過門檻的格數
            int lo = 1, hi = 100000;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (IceBreachMath.Evaluate(1f, -20f, mid, false, 3f, 1).ratio >= ratioWanted) hi = mid; else lo = mid + 1;
            }
            return IceBreachMath.Evaluate(1f, -20f, lo, false, 3f, 1).result;
        }
        Check(ResultAt(0.5f) == IceBreachResult.Stuck, "R < 0.75: stuck");
        Check(ResultAt(0.75f) == IceBreachResult.Partial, "R = 0.75: partial crack");
        Check(ResultAt(1.0f) == IceBreachResult.Surfaced, "R = 1.0: surfaced");
        Check(ResultAt(1.5f) == IceBreachResult.Violent, "R = 1.5: violent");
        Check(IceBreachMath.Evaluate(2.1f, -20f, 5000, false, 2f, 1).result == IceBreachResult.TooThick, "ice thicker than the frame: cannot try");
        Check(IceBreachMath.Evaluate(1.5f, -20f, 5000, false, IceBreachMath.FrameLimit(2f, 0.4f), 1).result == IceBreachResult.TooThick, "a frame below half health takes half the ice");
        Near(IceBreachMath.FrameLimit(2f, 0.6f), 2f, "a healthy frame keeps its limit");
        IceBreachOutcome two = IceBreachMath.Evaluate(1.2f, -20f, 192, false, 2f, 2);
        Near(two.ratio, first.ratio, "more frames do not change R");
        Near(two.damagePerFrame, first.damagePerFrame / 2f, "two frames share the damage");
        Near(IceBreachMath.Buoyancy(400) / IceBreachMath.Buoyancy(200), 2f, "lift is proportional to hull cells");
        Check(IceBreachMath.Evaluate(1.2f, -20f, 400, false, 2f, 1).ratio > first.ratio, "a bigger hull breaks thicker ice");

        // ── 重新結冰（§7.2）：−20 °C 時 0.3、2、3.2 天到 0.1、0.3、0.4 m；0 °C 以上不結冰 ──
        Near(IceBreachMath.DaysToFreeze(0.1f, -20f), 0.3f, "-20 °C: 0.1 m in 0.3 days", 0.05f);
        Near(IceBreachMath.DaysToFreeze(0.3f, -20f), 2.0f, "-20 °C: 0.3 m in 2 days", 0.1f);
        Near(IceBreachMath.DaysToFreeze(0.4f, -20f), 3.2f, "-20 °C: 0.4 m in 3.2 days", 0.1f);
        Check(float.IsPositiveInfinity(IceBreachMath.DaysToFreeze(0.1f, 0f)), "0 °C: never freezes");
        Near(IceBreachMath.FreezingDegreeDays(5f, 3f), 0f, "no frost above freezing");
        Near(IceBreachMath.RefrozenThickness(IceBreachMath.FreezingDegreeDays(-20f, 3.2f)), 0.4f, "3.2 days at -20 °C: 0.4 m", 0.01f);
    }

    static void IceCrackPatternTests()
    {
        const int W = 120, H = 140;
        List<(int x, int z)> hull = new List<(int x, int z)>();
        for (int x = 54; x < 66; x++)
            for (int z = 40; z < 100; z++)
                hull.Add((x, z));
        List<(int x, int z)> contacts = new List<(int x, int z)> { (60, 85) };
        HashSet<(int, int)> hullSet = new HashSet<(int, int)>(hull);
        float lc = IceBreachMath.CharacteristicLengthCells(1.2f);

        IceCrackPattern.Result a = IceCrackPattern.Generate(W, H, hull, contacts, lc, 1.25f, 1234);
        IceCrackPattern.Result b = IceCrackPattern.Generate(W, H, hull, contacts, lc, 1.25f, 1234);
        Check(a.cells.SequenceEqual(b.cells), "same seed, same pattern");
        IceCrackPattern.Result c = IceCrackPattern.Generate(W, H, hull, contacts, lc, 1.25f, 99);
        Check(!a.cells.SequenceEqual(c.cells), "another seed, another pattern");

        Check(hull.All(h => a.At(h.x, h.z) == IceCell.Hull), "hull cells stay hull (never crack or rubble)");
        Check(a.cells.Count(x => x == IceCell.Hull) == hull.Count, "only hull cells are hull");
        bool ringOpen = true;
        foreach ((int x, int z) in hull)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    (int, int) n = (x + dx, z + dz);
                    if (!hullSet.Contains(n) && a.At(n.Item1, n.Item2) != IceCell.OpenWater) ringOpen = false;
                }
        }
        Check(ringOpen, "open water surrounds the hull completely");
        Check(a.cells.Any(x => x == IceCell.Rubble), "a rubble band forms");
        Check(a.rubbleRadius == IceCrackPattern.RubbleRadius(lc, 1.25f) && a.rubbleRadius >= 3, $"rubble radius {a.rubbleRadius}");
        Check(a.radialCount == IceCrackPattern.RadialCount(1.25f) && a.radialCount == 7, $"R 1.25: seven radial cracks per contact ({a.radialCount})");
        Check(IceCrackPattern.RadialCount(3f) == 12 && IceCrackPattern.RadialCount(0.8f) == 6, "radial crack count is clamped to 6–12");
        Check(IceCrackPattern.RadialLength(lc, 1.25f, false) > 3f * lc && IceCrackPattern.RadialLength(lc, 0.9f, true) < IceCrackPattern.RadialLength(lc, 0.9f, false), "radial cracks run three ice lengths out, shorter when partial");

        // 裂紋跑到碎冰帶外面一段距離，而且在環狀裂紋半徑附近有裂紋
        float maxCrack = 0f;
        int nearRing = 0;
        for (int i = 0; i < a.cells.Length; i++)
        {
            if (a.cells[i] != IceCell.Crack) continue;
            maxCrack = System.Math.Max(maxCrack, a.distance[i]);
            if (System.Math.Abs(a.distance[i] - a.ringRadius) <= 1f) nearRing++;
        }
        Check(maxCrack > a.rubbleRadius + lc, $"radial cracks reach past the rubble band ({maxCrack:0.0} > {a.rubbleRadius + lc:0.0})");
        Check(nearRing > 10, $"a broken ring crack near r_c = {a.ringRadius:0.0} ({nearRing} cells)");
        Check(a.cells.Any(x => x == IceCell.OpenWater) && a.cells.Select((x, i) => (x, i)).Where(t => t.x == IceCell.OpenWater).All(t => a.distance[t.i] <= a.rubbleRadius + 1.5f),
            "open water only next to the hull and in gaps of the rubble band");
        int wedgeRubble = a.cells.Select((x, i) => (x, i)).Count(t => t.x == IceCell.Rubble && a.distance[t.i] > a.rubbleRadius + 1.5f);
        Check(wedgeRubble > 0, $"some wedges of ice beyond the rubble band are overturned ({wedgeRubble} cells)");

        // 出現時間：放射裂紋從接觸點往外長，環、碎冰與開放水在破裂瞬間出現
        bool breakTogether = a.cells.Select((x, i) => (x, i)).Where(t => t.x == IceCell.Rubble || t.x == IceCell.OpenWater).All(t => a.reveal[t.i] == IceCrackPattern.BreakTime);
        Check(breakTogether, "rubble and open water appear at the moment of the break");
        List<(float d, float t)> radial = new List<(float d, float t)>();
        for (int i = 0; i < a.cells.Length; i++)
        {
            if (a.cells[i] == IceCell.Crack && a.reveal[i] < IceCrackPattern.BreakTime)
            {
                int x = i % W, z = i / W;
                radial.Add(((float)System.Math.Sqrt((x - 60) * (x - 60) + (z - 85) * (z - 85)), a.reveal[i]));
            }
        }
        Check(radial.Count > 20 && radial.All(t => t.t >= IceCrackPattern.CrackStart), $"radial cracks start growing after {IceCrackPattern.CrackStart} s ({radial.Count} cells)");
        float nearT = radial.Where(t => t.d < 15f).Select(t => t.t).DefaultIfEmpty(0f).Average();
        float farT = radial.Where(t => t.d > 25f).Select(t => t.t).DefaultIfEmpty(99f).Average();
        Check(nearT < farT, $"cracks reach near cells before far ones ({nearT:0.00} s < {farT:0.00} s)");

        // 部分開裂：只有放射裂紋，沒有開放水與碎冰
        IceCrackPattern.Result partial = IceCrackPattern.Generate(W, H, hull, contacts, lc, 0.9f, 1234);
        Check(!partial.cells.Any(x => x == IceCell.OpenWater || x == IceCell.Rubble), "a partial crack leaves no open water or rubble");
        Check(partial.cells.Count(x => x == IceCell.Crack) > 4, "a partial crack still cracks the ice");
        Check(partial.cells.Count(x => x == IceCell.Crack) < a.cells.Count(x => x == IceCell.Crack), "a partial crack cracks less");

        // 猛烈出水：更多放射裂紋、更寬的碎冰帶
        IceCrackPattern.Result violent = IceCrackPattern.Generate(W, H, hull, contacts, lc, 2f, 1234);
        Check(violent.radialCount > a.radialCount && violent.rubbleRadius >= a.rubbleRadius, "violent surfacing: more cracks, wider rubble");

        // 距離場：船體 0、旁邊 1、斜角 √2
        float[] d = IceCrackPattern.DistanceField(10, 10, new List<(int x, int z)> { (5, 5) });
        Near(d[5 * 10 + 5], 0f, "distance 0 on the hull");
        Near(d[5 * 10 + 6], 1f, "distance 1 beside the hull");
        Near(d[6 * 10 + 6], 1.414f, "distance √2 diagonally", 0.01f);

        // 地圖邊緣不會出錯
        List<(int x, int z)> edgeHull = new List<(int x, int z)> { (0, 0), (1, 0), (0, 1), (1, 1) };
        IceCrackPattern.Result edge = IceCrackPattern.Generate(20, 20, edgeHull, null, lc, 1.5f, 5);
        Check(edge.At(0, 0) == IceCell.Hull, "a hull at the map corner still works");
    }
}
