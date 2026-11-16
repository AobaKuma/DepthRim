using System.Collections.Generic;
using DepthRim;
using Verse;

// 出港時船體放在航行地圖正中央：位移把船體範圍的中心移到地圖中心，不推出界外。
static partial class Test
{
    static void CenterVoyageHull()
    {
        List<IntVec3> deck = new List<IntVec3>();
        foreach (IntVec3 c in CellRect.FromLimits(10, 200, 18, 208)) { deck.Add(c); } // 9×9，靠地圖左上
        IntVec3 size = new IntVec3(250, 1, 250);
        IntVec3 o = DMSE.ShipSeparationUtility.CenteringOffset(deck, size);
        CellRect moved = CellRect.FromCellList(deck).MovedBy(o);
        Check(moved.CenterCell.x == 125 && moved.CenterCell.z == 125, $"a 9x9 deck near the edge is moved to the map centre ({moved.CenterCell})");
        Check(o.y == 0, "no vertical offset");

        List<IntVec3> wide = new List<IntVec3>();
        foreach (IntVec3 c in CellRect.FromLimits(0, 0, 9, 3)) { wide.Add(c); } // 10×4（偶數寬）
        CellRect movedWide = CellRect.FromCellList(wide).MovedBy(DMSE.ShipSeparationUtility.CenteringOffset(wide, size));
        Check(System.Math.Abs(movedWide.minX + movedWide.maxX - 249) <= 1 && System.Math.Abs(movedWide.minZ + movedWide.maxZ - 249) <= 1,
            $"an even-sized hull is centred to within a cell ({movedWide})");

        List<IntVec3> big = new List<IntVec3>();
        foreach (IntVec3 c in CellRect.FromLimits(0, 0, 29, 29)) { big.Add(c); }
        CellRect movedBig = CellRect.FromCellList(big).MovedBy(DMSE.ShipSeparationUtility.CenteringOffset(big, new IntVec3(30, 1, 30)));
        Check(movedBig.minX == 0 && movedBig.maxX == 29, "a hull as big as the map stays inside it");
        Check(DMSE.ShipSeparationUtility.CenteringOffset(new List<IntVec3>(), size) == IntVec3.Zero, "no hull, no offset");
    }
}
