using System;
using System.Collections.Generic;
using DepthRim;
using UnityEngine;

// 安全繩錨點（Docs/安全繩錨點_機制設計.md）：繩長範圍、遮罩、牆擋繩子、扣繩排序、懸垂曲線、惡劣海況的平均間隔。
static partial class Test
{
    static void RopeAnchorMath()
    {
        // ── 範圍 ──
        Check(RopeTetherMath.InReach(9, 0) && RopeTetherMath.InReach(0, -9), "9 cells straight out is in reach");
        Check(!RopeTetherMath.InReach(9, 1) && !RopeTetherMath.InReach(7, 7), "just past 9 cells is out of reach (9.06, 9.9)");
        Check(RopeTetherMath.InReach(6, 6), "6,6 (8.5 cells) is in reach");
        Check(RopeTetherMath.MaskIndex(0, 0) == RopeTetherMath.MaskSize / 2, "the anchor is the middle of the mask");
        Check(RopeTetherMath.MaskIndex(-9, -9) == 0 && RopeTetherMath.MaskIndex(9, 9) == RopeTetherMath.MaskSize - 1, "the mask corners");
        Check(RopeTetherMath.MaskIndex(10, 0) == -1 && RopeTetherMath.MaskIndex(0, -10) == -1, "outside the mask is -1");

        // ── 遮罩：沒有牆時就是圓 ──
        bool[] mask = new bool[RopeTetherMath.MaskSize];
        int open = RopeTetherMath.BuildMask(mask, (dx, dz) => false);
        int circle = 0;
        for (int dz = -9; dz <= 9; dz++) { for (int dx = -9; dx <= 9; dx++) { if (dx * dx + dz * dz <= 81) { circle++; } } }
        Check(open == circle, $"with no walls the mask is the whole circle ({open} cells)");

        // 東邊 3 格處一道南北向的牆（x = 3，z -9..9）：牆和牆後面都拉不到，牆前面可以
        Func<int, int, bool> wall = (dx, dz) => dx == 3;
        RopeTetherMath.BuildMask(mask, wall);
        Check(mask[RopeTetherMath.MaskIndex(2, 0)] && mask[RopeTetherMath.MaskIndex(-8, 3)], "cells before the wall are covered");
        Check(!mask[RopeTetherMath.MaskIndex(3, 0)] && !mask[RopeTetherMath.MaskIndex(5, 0)] && !mask[RopeTetherMath.MaskIndex(6, 4)], "the wall and the cells behind it are not");
        Check(mask[RopeTetherMath.MaskIndex(0, 0)], "the anchor's own cell is always covered");

        // 遮罩和逐格呼叫 RopePasses 的結果完全相同（§7.9）
        Func<int, int, bool> scattered = (dx, dz) => (dx * 7 + dz * 13) % 11 == 0 && (dx != 0 || dz != 0);
        RopeTetherMath.BuildMask(mask, scattered);
        int mismatches = 0;
        for (int dz = -9; dz <= 9; dz++)
        {
            for (int dx = -9; dx <= 9; dx++)
            {
                bool expect = RopeTetherMath.InReach(dx, dz) && (dx == 0 && dz == 0 || !scattered(dx, dz) && RopeTetherMath.RopePasses(dx, dz, scattered));
                if (mask[RopeTetherMath.MaskIndex(dx, dz)] != expect) { mismatches++; }
            }
        }
        Check(mismatches == 0, $"the mask matches cell-by-cell RopePasses ({mismatches} mismatches)");

        // 直線：只看中間的格子，不看兩端；相鄰格永遠拉得到
        Check(RopeTetherMath.RopePasses(1, 0, (x, z) => true) && RopeTetherMath.RopePasses(1, 1, (x, z) => true), "a neighbouring cell has nothing in between");
        List<(int, int)> visited = new List<(int, int)>();
        RopeTetherMath.RopePasses(4, 2, (x, z) => { visited.Add((x, z)); return false; });
        Check(visited.Count >= 3 && !visited.Contains((0, 0)) && !visited.Contains((4, 2)), $"the line checks only the cells in between ({visited.Count})");
        bool contiguous = true;
        (int px, int pz) = (0, 0);
        foreach ((int x, int z) in visited) { if (Math.Abs(x - px) > 1 || Math.Abs(z - pz) > 1) { contiguous = false; } (px, pz) = (x, z); }
        Check(contiguous, "the line steps one cell at a time");

        // ── 扣繩排序 ──
        Check(RopeTetherMath.Compare(TetherRank.Colonist, 80, TetherRank.Mech, 1) < 0, "a colonist far away goes before a mech close by");
        Check(RopeTetherMath.Compare(TetherRank.Mech, 50, TetherRank.Prisoner, 1) < 0 && RopeTetherMath.Compare(TetherRank.Prisoner, 50, TetherRank.Animal, 1) < 0, "mechs before prisoners before animals");
        Check(RopeTetherMath.Compare(TetherRank.Colonist, 4, TetherRank.Colonist, 9) < 0, "same kind: the nearer first");
        List<(TetherRank, int)> list = new List<(TetherRank, int)> { (TetherRank.Animal, 1), (TetherRank.Colonist, 64), (TetherRank.Prisoner, 2), (TetherRank.Colonist, 4) };
        list.Sort((a, b) => RopeTetherMath.Compare(a.Item1, a.Item2, b.Item1, b.Item2));
        Check(list[0] == (TetherRank.Colonist, 4) && list[1] == (TetherRank.Colonist, 64) && list[3] == (TetherRank.Animal, 1), "sorting puts both colonists first, the animal last");
        Check(RopeTetherMath.MaxTethers == 2, "two lines per anchor");

        // ── 懸垂 ──
        Near(RopeTetherMath.Sag(0f), RopeTetherMath.MaxSag, "slack line: most sag");
        Near(RopeTetherMath.Sag(RopeTetherMath.RopeLength), 0f, "taut line: no sag");
        Check(RopeTetherMath.Sag(3f) > RopeTetherMath.Sag(6f), "the further away, the tauter");
        Vector3 from = new Vector3(1f, 0f, 1f), to = new Vector3(6f, 0f, 3f), drift = new Vector3(0.3f, 0f, 0f);
        Check((RopeTetherMath.Point(from, to, 0f, 0.5f, drift) - from).magnitude < 1e-5f && (RopeTetherMath.Point(from, to, 1f, 0.5f, drift) - to).magnitude < 1e-5f, "the ends stay on the anchor and the pawn");
        Vector3 mid = RopeTetherMath.Point(from, to, 0.5f, 0.5f, Vector3.zero);
        Near(mid.z, 2f - 0.5f, "the middle sags south by the sag");
        Near(RopeTetherMath.Point(from, to, 0.5f, 0f, drift).x, 3.5f + 0.3f, "the middle drifts with the water");

        // ── 惡劣海況 ──
        Check(!RopeTetherMath.IsRoughSea(1.79f) && RopeTetherMath.IsRoughSea(1.8f), "rough sea from weather factor 1.8");
        Check(float.IsPositiveInfinity(RopeTetherMath.RoughSeaMtbHours(1.5f, true, false, false)), "calm sea: never swept");
        Near(RopeTetherMath.RoughSeaMtbHours(1.8f, true, false, false), 12f, "factor 1.8: every 12 h on average");
        Near(RopeTetherMath.RoughSeaMtbHours(2.0f, true, false, false), 6f, "factor 2.0: 6 h");
        Near(RopeTetherMath.RoughSeaMtbHours(2.5f, true, false, false), 1.5f, "factor 2.5: 1.5 h");
        Near(RopeTetherMath.RoughSeaMtbHours(3f, true, false, false), 1.5f, "beyond 2.5: capped at the last point");
        float m19 = RopeTetherMath.RoughSeaMtbHours(1.9f, true, false, false);
        Near(1f / m19, (1f / 12f + 1f / 6f) / 2f, "in between: the rate is interpolated");
        float baseMtb = RopeTetherMath.RoughSeaMtbHours(2f, true, false, false);
        Near(RopeTetherMath.RoughSeaMtbHours(2f, false, false, false), baseMtb / RopeTetherMath.InnerDeckFactor, "mid-deck is safer");
        Near(RopeTetherMath.RoughSeaMtbHours(2f, true, true, false), baseMtb / RopeTetherMath.DownedFactor, "downed: three times as likely");
        Near(RopeTetherMath.RoughSeaMtbHours(2f, true, false, true), baseMtb / RopeTetherMath.LowMovingFactor, "slow movers: 1.5 times as likely");
        float chance = RopeTetherMath.ChancePerCheck(6f, 250);
        Check(chance > 0.0164f && chance < 0.0168f, $"chance per 250-tick check at a 6 h MTB ({chance:F4})");
        Near(RopeTetherMath.ChancePerCheck(float.PositiveInfinity, 250), 0f, "no chance in a calm sea");
    }
}
