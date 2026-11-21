using DepthRim;

// 艦橋分頁（ITab_Vessel）的計算。
static partial class Test
{
    static void VesselTabMathTests()
    {
        Check(VesselTabMath.EtaTicks(3, 0f, 1000, 1f) == 3000, "three tiles at full thrust take three tiles' time");
        Check(VesselTabMath.EtaTicks(3, 0.5f, 1000, 1f) == 2500, "progress through the current tile is subtracted");
        Check(VesselTabMath.EtaTicks(2, 0f, 1000, 0.5f) == 4000, "half thrust takes twice as long");
        Check(VesselTabMath.EtaTicks(0, 0f, 1000, 1f) == 0 && VesselTabMath.EtaTicks(3, 0f, int.MaxValue, 1f) == 0, "nothing left or no speed: no ETA");
    }
}
