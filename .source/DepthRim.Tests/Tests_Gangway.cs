using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;
using Verse;

// 可收放舷梯的跨距（GangwayMath.TryFindSpan）與開放海面可搭橋。
static partial class Test
{
    static void Gangway()
    {
        // 一條線上：x = 0 是舷梯，1..gap 是海，gap+1 是對岸；往東數
        IntVec3 east = new IntVec3(1, 0, 0);
        List<IntVec3> span = new List<IntVec3>();
        bool Find(int gap, int reach, out IntVec3 landing, int landAt = -1)
        {
            int land = landAt < 0 ? gap + 1 : landAt;
            return GangwayMath.TryFindSpan(IntVec3.Zero, east, reach, c => c.x >= 1 && c.x <= gap, c => c.x == land, span, out landing);
        }

        Check(Find(4, 6, out IntVec3 l4) && span.Count == 4 && l4 == new IntVec3(5, 0, 0), $"4 cells of sea: span 4, landing at x=5 ({span.Count}, {l4})");
        Check(span.Select(c => c.x).SequenceEqual(new[] { 1, 2, 3, 4 }), "the span runs near to far");
        Check(Find(6, 6, out IntVec3 l6) && span.Count == 6 && l6.x == 7, "exactly the max reach still connects");
        Check(!Find(7, 6, out IntVec3 l7) && span.Count == 0 && !l7.IsValid, "one cell past the reach does not, and leaves the span empty");
        Check(!Find(0, 6, out _), "no sea in front (a deck right there): nothing to span");
        Check(!Find(3, 6, out _, landAt: 99), "sea that ends in something that is not a deck (a wall, a pillar) does not connect");
        Check(GangwayMath.TryFindSpan(IntVec3.Zero, new IntVec3(0, 0, -1), 6, c => c.z <= -1 && c.z >= -2, c => c.z == -3, span, out IntVec3 south) && south == new IntVec3(0, 0, -3), "works facing south too");

        // 開放海面可蓋原版的橋（Bridgeable）；高空（平台層）不行
        XDocument sea = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "SeaSurface", "SeaSurface.xml"));
        XElement Terrain(string name) => sea.Root.Elements("TerrainDef").First(e => (string)e.Element("defName") == name);
        Check(Terrain("DepthRim_OpenSea").Element("affordances").Elements("li").Any(e => e.Value == "Bridgeable"), "open sea is bridgeable");
        Check(!Terrain("DepthRim_OpenAir").Element("affordances").Elements("li").Any(e => e.Value == "Bridgeable"), "open air (platform level) is not");

        // 舷梯的臨時甲板是地基（底下還是開放海面，龍骨樁不會把它算進船體）
        XDocument gw = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "SeaSurface", "Gangway.xml"));
        XElement deck = gw.Root.Elements("TerrainDef").First(e => (string)e.Element("defName") == "DepthRim_GangwayDeck");
        Check((string)deck.Element("isFoundation") == "true" && deck.Element("designationCategory") == null, "the gangway deck is a foundation nobody can build by hand");
        Check((int)deck.Element("renderPrecedence") < 400, "its render order is below the vanilla limit of 400");
    }
}
