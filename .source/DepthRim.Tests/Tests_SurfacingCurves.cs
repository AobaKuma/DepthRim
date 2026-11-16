using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 出水演出的時間曲線（原版 SimpleCurve）與水花 fleck：船體破水後很快出現、衝過頭再落回原尺寸、彈一下回到原位；
// 劈開的白浪往外推、越推越慢、最後散掉；水花畫在 DMSE 船體（renderQueue 3200）上面。
static partial class Test
{
    static void SurfacingCurvesTests()
    {
        Near(SurfacingCurves.ShipAlpha.Evaluate(0f), 0f, "the clear hull is hidden at the moment of breaking");
        Near(SurfacingCurves.ShipAlpha.Evaluate(5f), 1f, "the hull is fully visible after emerging");
        Check(SurfacingCurves.ShipAlpha.Evaluate(1f) > 0.9f, "the hull shows within about a second (the splash covers the fade)");
        Near(SurfacingCurves.ShipTint.Evaluate(5f), 1f, "the hull gets its own colour back");

        Near(SurfacingCurves.ShipScale.Evaluate(0f), 0.96f, "the scale starts at the submerged size");
        Check(SurfacingCurves.ShipScale.MaxY > 1f, "the hull overshoots a little as it pops out");
        Near(SurfacingCurves.ShipScale.Evaluate(10f), 1f, "and settles at its real size");

        Check(SurfacingCurves.ShipLift.MaxY > 0.15f, $"the bow lifts as it breaks the surface ({SurfacingCurves.ShipLift.MaxY:F2})");
        Near(SurfacingCurves.ShipLift.Evaluate(10f), 0f, "the lift settles back to zero");

        Near(SurfacingCurves.SplashRate.Evaluate(10f), 0f, "the splashes stop");

        Near(SurfacingCurves.SweepSeconds(1), 0.6f, "even a tiny hull takes long enough to see bow before stern");
        Near(SurfacingCurves.SweepSeconds(200), 1.4f, "a long hull is split in at most 1.4 s");
        Check(SurfacingCurves.SweepSeconds(30) > SurfacingCurves.SweepSeconds(10), "a longer hull takes longer to split");
        Check(SurfacingCurves.StripRevealDelay > 0f && SurfacingCurves.ShipAlpha.Evaluate(-SurfacingCurves.StripRevealDelay) == 0f, "each part of the hull stays hidden until the splash there has gone up");

        // 水花是原版 GroundWaterSplash、WaterChurnNoise 的原樣複本，只多了 renderQueue（畫在過場船體 3200 上面）
        string vanilla = System.IO.Path.Combine(ModRoot, "..", "..", "Data", "Core", "Defs", "Effects", "Fleck_Visual.xml");
        var vanillaDefs = XDocument.Load(vanilla).Root.Elements().ToList();
        var all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        foreach ((string ours, string theirs) in new[] { ("DepthRim_SurfacingSplash", "GroundWaterSplash") })
        {
            XElement def = all.SingleOrDefault(e => e.Name.LocalName == "FleckDef" && (string)e.Element("defName") == ours);
            Check(def != null, $"{ours} exists");
            if (def == null)
            {
                continue;
            }
            XElement copy = new XElement(def);
            copy.Element("defName").Value = theirs;
            List<XElement> queues = copy.Descendants("renderQueue").ToList();
            Check(queues.Count > 0 && queues.All(q => (int)q > 3200), $"{ours} draws above the cutscene hull (3200)");
            queues.ForEach(q => q.Remove());
            // 水柱會轉到船體法線方向：錨點改成中心（原版 (.5,.2) 的錨點補償不跟著旋轉）
            Check(((string)copy.Element("scalingAnchor"))?.Replace(" ", "") == "(.5,.5,0)", $"{ours} scales from its centre so it can be rotated");
            XElement original = new XElement(vanillaDefs.Single(e => (string)e.Element("defName") == theirs));
            copy.Element("scalingAnchor")?.Remove();
            original.Element("scalingAnchor")?.Remove();
            Check(XNode.DeepEquals(Strip(copy), Strip(original)), $"{ours} is otherwise identical to vanilla {theirs}");
        }
    }

    static XElement Strip(XElement e)
    {
        XElement c = new XElement(e);
        c.DescendantNodes().OfType<XComment>().ToList().ForEach(n => n.Remove());
        foreach (XText t in c.DescendantNodes().OfType<XText>().ToList())
        {
            if (string.IsNullOrWhiteSpace(t.Value)) { t.Remove(); } else { t.Value = t.Value.Trim(); }
        }
        return c;
    }
}
