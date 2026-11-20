using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 下潛能力 M6（Docs/海岸船舶與潛水鐘_機制設計.md §2.1、§2.2、§2.4）：壓載是唯一門檻、不夠就變慢；耐壓改成警告。
static partial class Test
{
    static void BallastMathTests()
    {
        Check(BallastMath.Capacity(0) == 0 && BallastMath.Capacity(2) == 2 * BallastMath.CellsPerTank, "each tank lifts a fixed number of hull cells");
        Near(BallastMath.Slowdown(100, 1), 1f, "a hull the ballast can lift changes depth at full speed");
        Near(BallastMath.Slowdown(BallastMath.CellsPerTank, 1), 1f, "exactly at capacity is still full speed");
        Near(BallastMath.Slowdown(BallastMath.CellsPerTank * 2, 1), 2f, "twice the hull for the ballast takes twice as long");
        Near(BallastMath.Slowdown(BallastMath.CellsPerTank * 10, 1), BallastMath.MaxSlowdown, "never slower than the cap");
        Near(BallastMath.Slowdown(50, 0), BallastMath.MaxSlowdown, "no ballast at all is the slowest");
        Check(BallastMath.TicksPerTier(100, 1) == DiveMath.TicksPerTier, "full speed is an hour per tier");
        Check(BallastMath.TicksPerTier(BallastMath.CellsPerTank * 3, 2) == (int)System.Math.Round(DiveMath.TicksPerTier * 1.5f), "three hulls' worth on two tanks is 1.5x");
        Check(!BallastMath.Short(187, 1) && BallastMath.Short(996, 4) && !BallastMath.Short(996, 5), "one tank lifts the patrol submarine (187 cells); the start submarine (996 cells) needs five");

        // 耐壓警告的數字
        Check(HullWarning.Excess(DepthTier.Deep, DepthTier.Thermocline) == 1 && HullWarning.Excess(DepthTier.Shallow, DepthTier.Deep) == 0, "the warning counts tiers over the rating");
        Near(HullWarning.DamagePerDay(DepthTier.Thermocline, DepthTier.Thermocline), 0f, "no damage within the rating");
        Check(HullWarning.DamagePerDay(DepthTier.Deep, DepthTier.Shallow) > HullWarning.DamagePerDay(DepthTier.Thermocline, DepthTier.Shallow),
            "deeper over the rating crushes faster");
        Near(HullWarning.DamagePerDay(DepthTier.Deep, DepthTier.Shallow),
            WallPressureTopology.DamagePerUpdate(WallPressureTopology.Capacity(DepthTier.Deep), WallPressureTopology.Capacity(DepthTier.Shallow)) * 240f,
            "the damage per day is the wall-pressure damage over a day of updates");
    }

    static void ContentBallast()
    {
        List<XElement> all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        XElement Def(string name) => all.FirstOrDefault(d => (string)d.Element("defName") == name);
        XElement tank = Def("DepthRim_BallastTank");
        if (!Check(tank != null, "DepthRim_BallastTank exists")) { return; }
        Check((string)tank.Element("size") == "(1,2)", "a ballast tank is 1x2");
        Check((string)tank.Element("designationCategory") == "DepthRim_Underwater", "it is in the underwater build menu");
        XElement prefab = Def("DepthRim_Prefab_PatrolSubmarine");
        int tanks = prefab?.Descendants("li").Where(li => (string)li.Element("def") == "DepthRim_BallastTank").SelectMany(li => li.Element("posList")?.Elements("li") ?? Enumerable.Empty<XElement>()).Count() ?? 0;
        Check(tanks == 2, $"the prefab patrol submarine carries two ballast tanks ({tanks})");
    }
}
