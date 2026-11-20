using System.Collections.Generic;
using System.Linq;
using DepthRim;
using UnityEngine;
using Verse;

// 靠岸（海岸造船 M3）：旋轉船體格、建築旋轉後的位置、淨空與進場路線。
static partial class Test
{
    static void DockingMathTests()
    {
        IntVec3 p = new IntVec3(10, 0, 10);
        Check(DockingMath.RotateCell(new IntVec3(10, 0, 12), p, 1) == new IntVec3(12, 0, 10), "a quarter turn clockwise takes north to east");
        Check(DockingMath.RotateCell(new IntVec3(10, 0, 12), p, 2) == new IntVec3(10, 0, 8), "half a turn takes north to south");
        Check(DockingMath.RotateCell(new IntVec3(10, 0, 12), p, 3) == new IntVec3(8, 0, 10), "three quarters takes north to west");
        Check(DockingMath.RotateCell(new IntVec3(13, 0, 7), p, 4) == new IntVec3(13, 0, 7), "four quarter turns is no turn");
        Check(DockingMath.RotateCell(DockingMath.RotateCell(new IntVec3(3, 0, 17), p, 1), p, 3) == new IntVec3(3, 0, 17), "turning back undoes the turn");
        Check(DockingMath.RotateRot(Rot4.North, 1) == Rot4.East && DockingMath.RotateRot(Rot4.West, 1) == Rot4.North && DockingMath.RotateRot(Rot4.East, 2) == Rot4.West, "rotations add up");

        // 2×1 建築（例如排水泵）：可以轉的話佔地跟著轉；不能轉的轉 90° 沒有解、180° 照樣
        IntVec2 size = new IntVec2(2, 1);
        IntVec3 pos = new IntVec3(10, 0, 12);
        CellRect before = GenAdj.OccupiedRect(pos, Rot4.North, size);
        Check(DockingMath.RotatedPlacement(pos, Rot4.North, size, true, p, 1, out IntVec3 np, out Rot4 nr), "a rotatable 2x1 building turns");
        CellRect after = GenAdj.OccupiedRect(np, nr, size);
        HashSet<IntVec3> expected = new HashSet<IntVec3>(before.Cells.Select(c => DockingMath.RotateCell(c, p, 1)));
        Check(nr == Rot4.East && expected.SetEquals(after.Cells), $"its cells turn with the hull ({after} vs {string.Join(",", expected)})");
        Check(!DockingMath.RotatedPlacement(pos, Rot4.North, size, false, p, 1, out _, out _), "a non-rotatable 2x1 building blocks a quarter turn");
        Check(DockingMath.RotatedPlacement(pos, Rot4.North, size, false, p, 2, out IntVec3 np2, out Rot4 nr2) && nr2 == Rot4.North
              && new HashSet<IntVec3>(before.Cells.Select(c => DockingMath.RotateCell(c, p, 2))).SetEquals(GenAdj.OccupiedRect(np2, nr2, size).Cells),
            "a non-rotatable 2x1 building still turns half way (same shape)");
        Check(DockingMath.RotatedPlacement(new IntVec3(9, 0, 9), Rot4.North, new IntVec2(3, 3), false, p, 1, out _, out _), "a square non-rotatable building can turn a quarter");

        // 淨空：中間一格陸地
        int[] clear = DockingMath.Clearance(9, 9, (x, z) => !(x == 4 && z == 4));
        Check(clear[4 * 9 + 4] == 0 && clear[4 * 9 + 5] == 1 && clear[4 * 9 + 7] == 3 && clear[0] == 4, "clearance is the Chebyshev distance to land; the map edge is open sea");

        // 一個朝南開口的灣：陸地在北半邊，灣在中間；泊位 3×3 貼著灣底
        const int W = 40, H = 40;
        bool Water(int x, int z)
        {
            if (z < 20) { return true; }                 // 南半邊是海
            return x >= 14 && x <= 26 && z <= 32;        // 灣：13 格寬，伸進北邊
        }
        List<IntVec3> berth = CellRect.FromLimits(19, 29, 21, 31).Cells.ToList();
        DockingMath.Approach a = DockingMath.Plan(W, H, Water, berth, 3, 3);
        Check(a.Ok, $"a ship can come into the bay ({a.failure})");
        Check(a.entry.z == 0 || a.entry.x == 0 || a.entry.x == W - 1, $"it enters from the open map edge ({a.entry})");
        Check(a.path.All(c => Water(c.x, c.z)), "the whole route is on water");
        Check(a.path[a.path.Count - 1] == a.turn && a.corners[a.corners.Count - 1] == a.berth, "the route ends at the turning point, then the berth");
        Check(a.initialHeading.sqrMagnitude > 0.99f, "the ship enters bow first along the route");

        // 灣中央的泊位（四周都是水）：迴轉點在靠外海（南邊）那一側，不會先開過泊位再倒回來（2026-11-20 演出截圖）
        List<IntVec3> midBerth = CellRect.FromLimits(19, 22, 21, 24).Cells.ToList();
        bool WideBay(int x, int z) => z < 20 || (x >= 8 && x <= 32 && z <= 36);
        DockingMath.Approach mid = DockingMath.Plan(W, H, WideBay, midBerth, 3, 3);
        Check(mid.Ok && mid.turn.z <= 23, $"in open water the turning point is on the seaward side of the berth ({mid.turn})");

        // 灣口太窄：船進不來
        bool Narrow(int x, int z)
        {
            if (z < 20) { return true; }
            if (z < 24) { return x == 20; }               // 一格寬的水道
            return x >= 14 && x <= 26 && z <= 32;
        }
        Check(DockingMath.Plan(W, H, Narrow, berth, 3, 3).failure == DockingMath.PlanFailure.NoRoute, "a channel narrower than the ship blocks the route");

        // 灣太小：沒有迴轉空間
        bool Pond(int x, int z) => x >= 19 && x <= 22 && z >= 29 && z <= 32; // 4×4：最大淨空 2，3×3 的船要 3
        Check(DockingMath.Plan(W, H, Pond, berth, 3, 3).failure == DockingMath.PlanFailure.NoTurningRoom, "a pond too small to turn in has no turning room");

        Check(DockingMath.PassClearance(9, 5) == 3 && DockingMath.TurnClearance(9, 5) == 6, "pass clearance is half the beam, turn clearance half the diagonal");
        List<IntVec3> zig = new List<IntVec3> { new IntVec3(0, 0, 0), new IntVec3(1, 0, 0), new IntVec3(2, 0, 0), new IntVec3(2, 0, 1), new IntVec3(2, 0, 2) };
        Check(DockingMath.Corners(zig).SequenceEqual(new[] { new IntVec3(0, 0, 0), new IntVec3(2, 0, 0), new IntVec3(2, 0, 2) }), "the route keeps only its corners");
    }
}
