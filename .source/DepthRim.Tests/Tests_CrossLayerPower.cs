using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 跨層電力（M5.5，Docs/海岸船舶與潛水鐘_機制設計.md §7.10）：支柱腳 → 支柱頂單向，容量 2000 W、線損 5%。
static partial class Test
{
    static void CrossLayerPowerMath()
    {
        CrossLayerMath.Part On(float w) => new CrossLayerMath.Part(w, true, false);
        CrossLayerMath.Part Wants(float w) => new CrossLayerMath.Part(w, false, true);
        CrossLayerMath.Part Off(float w) => new CrossLayerMath.Part(w, false, false);

        // ── 收端缺多少 ──
        Near(CrossLayerMath.Need(new[] { On(-300f), On(-200f) }, false, 2000f), 500f, "the platform needs what its consumers draw");
        Near(CrossLayerMath.Need(new[] { On(-300f), On(100f) }, false, 2000f), 200f, "its own generators cover part of it");
        Near(CrossLayerMath.Need(new[] { On(-300f), On(500f) }, false, 2000f), 0f, "a platform that makes enough needs nothing");
        Near(CrossLayerMath.Need(new[] { Wants(-300f) }, false, 2000f), 300f, "consumers waiting for power count, so vanilla can switch them on");
        Near(CrossLayerMath.Need(new[] { Off(-300f) }, false, 2000f), 0f, "switched-off consumers do not count");
        Near(CrossLayerMath.Need(new[] { Off(400f), On(-100f) }, false, 2000f), 100f, "a generator that is off makes nothing");
        Near(CrossLayerMath.Need(new[] { On(-100f) }, true, 2000f), 2100f, "a battery that is not full takes the spare capacity");

        // ── 送端剩多少 ──
        Near(CrossLayerMath.Surplus(new[] { On(1000f), On(-300f) }, false), 700f, "the water level spares what it makes beyond its own use");
        Near(CrossLayerMath.Surplus(new[] { On(100f), On(-300f) }, false), 0f, "a net short of power spares nothing");
        Near(CrossLayerMath.Surplus(new[] { On(1000f), Wants(-800f) }, false), 1000f, "consumers that are off do not reduce the surplus");
        Check(float.IsPositiveInfinity(CrossLayerMath.Surplus(new[] { On(-300f) }, true)), "stored battery power makes the source unlimited (capacity still caps it)");

        // ── 線損 ──
        Near(CrossLayerMath.Draw(950f, 0.05f), 1000f, "sending 950 W costs 1000 W at 5% line loss");
        Near(CrossLayerMath.Draw(0f, 0.05f), 0f, "sending nothing costs nothing");
        Near(CrossLayerMath.Draw(100f, 0f), 100f, "no loss: draw equals delivery");

        // ── 分配 ──
        float[] one = CrossLayerMath.Allocate(500f, new List<(int, float)> { (0, 2000f) }, new Dictionary<int, float> { [0] = 10000f }, 0.05f);
        Near(one[0], 500f, "a single pillar sends what the platform needs");
        float[] capped = CrossLayerMath.Allocate(5000f, new List<(int, float)> { (0, 2000f) }, new Dictionary<int, float> { [0] = float.PositiveInfinity }, 0.05f);
        Near(capped[0], 2000f, "never more than the capacity");
        float[] poor = CrossLayerMath.Allocate(5000f, new List<(int, float)> { (0, 2000f) }, new Dictionary<int, float> { [0] = 1000f }, 0.05f);
        Near(poor[0], 950f, "a weak source sends its surplus minus line loss");
        Near(CrossLayerMath.Draw(poor[0], 0.05f), 1000f, "and the draw uses exactly that surplus");
        Dictionary<int, float> shared = new Dictionary<int, float> { [0] = 3000f };
        float[] two = CrossLayerMath.Allocate(2500f, new List<(int, float)> { (0, 2000f), (0, 2000f) }, shared, 0.05f);
        Near(two[0] + two[1], 2500f, "two pillars on one platform net split the need instead of each covering it");
        Near(two[0], 2000f, "the first one fills up to its capacity");
        Check(shared[0] < 3000f - 2500f / 0.95f + 1f && shared[0] >= 0f, "their shared source net is charged for both");
        float[] starved = CrossLayerMath.Allocate(2500f, new List<(int, float)> { (0, 2000f), (0, 2000f) }, new Dictionary<int, float> { [0] = 1050f }, 0.05f);
        Near(starved[0] + starved[1], 997.5f, "pillars sharing one source net cannot spend its surplus twice");
        float[] separate = CrossLayerMath.Allocate(3000f, new List<(int, float)> { (0, 2000f), (1, 2000f) }, new Dictionary<int, float> { [0] = 1050f, [1] = 10000f }, 0.05f);
        Near(separate[0], 997.5f, "each pillar is limited by its own source net");
        Near(separate[1], 2000f, "the other pillar makes up the rest up to its capacity");
        float[] none = CrossLayerMath.Allocate(500f, new List<(int, float)> { (-1, 2000f) }, new Dictionary<int, float>(), 0.05f);
        Near(none[0], 0f, "a pillar foot with no power net sends nothing");
        Near(CrossLayerMath.Allocate(0f, new List<(int, float)> { (0, 2000f) }, new Dictionary<int, float> { [0] = 9999f }, 0.05f)[0], 0f,
            "nothing is sent when nothing is needed");
    }

    static void ContentCrossLayerPower()
    {
        List<XElement> all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        foreach (string name in new[] { "DepthRim_PlatformPillar", "DepthRim_PlatformPillarTop" })
        {
            XElement def = all.FirstOrDefault(d => (string)d.Element("defName") == name);
            if (!Check(def != null, $"{name} exists")) { continue; }
            List<XElement> comps = def.Element("comps")?.Elements("li").ToList() ?? new List<XElement>();
            XElement power = comps.FirstOrDefault(c => (string)c.Attribute("Class") == "CompProperties_Power");
            // 原版 ThingDef.ConnectToPower 只認確切的 CompPowerTrader，子類別接不上電線
            Check(power != null && (string)power.Element("compClass") == "CompPowerTrader", $"{name} has a plain CompPowerTrader so it connects to conduits");
            Check(power != null && (float?)power.Element("basePowerConsumption") == 0f, $"{name} uses no power of its own");
            XElement link = comps.FirstOrDefault(c => (string)c.Attribute("Class") == "DepthRim.CompProperties_CrossLayerPower");
            Check(link != null && (float?)link.Element("capacity") == 2000f && (float?)link.Element("lineLoss") == 0.05f,
                $"{name} carries the cross-layer link (2000 W, 5% loss)");
        }
    }
}
