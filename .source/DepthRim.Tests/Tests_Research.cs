using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

// 科技需求（2026-11-17）：每個玩家能蓋的建築／地形（有 designationCategory）與能製作的物品（recipeMaker）都要有研究前置，
// 而且引用的 DepthRim 研究都存在。切石台那類配方（RecipeDef）由工作台本身的研究把關，不檢查。
static partial class Test
{
    static void ContentResearch()
    {
        List<XElement> all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        HashSet<string> research = new HashSet<string>(all.Where(d => d.Name.LocalName == "ResearchProjectDef").Select(d => (string)d.Element("defName")));
        Dictionary<string, XElement> named = new Dictionary<string, XElement>();
        foreach (XElement d in all)
        {
            string n = (string)d.Attribute("Name");
            if (n != null) { named[n] = d; }
        }
        XElement Inherited(XElement d, string tag)
        {
            for (XElement cur = d; cur != null; cur = (string)cur.Attribute("ParentName") is string p && named.TryGetValue(p, out XElement parent) ? parent : null)
            {
                XElement e = cur.Element(tag);
                if (e != null) { return e; }
            }
            return null;
        }
        int checkedDefs = 0;
        foreach (XElement d in all)
        {
            if ((string)d.Attribute("Abstract") == "True") { continue; }
            string name = (string)d.Element("defName");
            List<string> req = null;
            if (Inherited(d, "designationCategory") != null)
            {
                req = Inherited(d, "researchPrerequisites")?.Elements("li").Select(li => li.Value).ToList() ?? new List<string>();
            }
            XElement maker = Inherited(d, "recipeMaker");
            if (maker != null)
            {
                req = maker.Elements("researchPrerequisites").SelectMany(e => e.Elements("li")).Select(li => li.Value)
                    .Concat(maker.Elements("researchPrerequisite").Select(e => e.Value)).ToList();
            }
            if (req == null) { continue; }
            checkedDefs++;
            Check(req.Count > 0, $"{name} has a research requirement");
            foreach (string r in req.Where(r => r.StartsWith("DepthRim_")))
            {
                Check(research.Contains(r), $"{name}: research {r} exists");
            }
        }
        Check(checkedDefs >= 20, $"found buildable and craftable defs to check ({checkedDefs})");
    }
}
