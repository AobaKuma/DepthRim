using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 海底採樣器（2026-11-17）：從冷泉、海底火山、熱液泉提取海底研究點數（FFF 知識類別），每個地標有總量上限。
// 除了初始科技「水下生存」，其他水下科技都是海底研究分頁的知識型專案。
static partial class Test
{
    static void SeafloorSampler()
    {
        // 一般情況：想提取多少就給多少，乘上難度倍率
        Near(SeafloorSamplerMath.Step(1.5f, 100f, 1f, out float drawn), 1.5f, "a full step is injected");
        Near(drawn, 1.5f, "a full step is drawn from the site");
        Near(SeafloorSamplerMath.Step(1.5f, 100f, 2f, out drawn), 3f, "difficulty research speed multiplies what is injected");
        Near(drawn, 1.5f, "difficulty does not change what the site loses");
        // 地標快採完：只給剩下的
        Near(SeafloorSamplerMath.Step(1.5f, 0.4f, 1f, out drawn), 0.4f, "a nearly exhausted site gives only what is left");
        Near(SeafloorSamplerMath.Step(1.5f, 0f, 1f, out drawn), 0f, "an exhausted site gives nothing");
        Near(drawn, 0f, "an exhausted site loses nothing");

        List<XElement> all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        XElement Def(string name) => all.FirstOrDefault(d => (string)d.Element("defName") == name);

        XElement def = Def("DR_SeafloorSampler");
        if (!Check(def != null, "DR_SeafloorSampler exists")) { return; }
        Check((string)def.Element("size") == "(2,1)", "seafloor sampler is 2x1");
        Check(def.Element("researchPrerequisites")?.Elements("li").Any(li => li.Value == "DepthRim_UnderwaterSurvival") == true,
            "the sampler is unlocked by the starting tech (otherwise seabed research could never begin)");
        XElement comp = def.Element("comps")?.Elements("li").FirstOrDefault(li => (string)li.Attribute("Class") == "DepthRim.CompProperties_SeafloorSampler");
        if (!Check(comp != null, "seafloor sampler has its comp")) { return; }
        Check((string)comp.Element("knowledgeCategory") == "DepthRim_Seabed", "the sampler produces seabed research");
        string[] sources = comp.Element("sources").Elements("li").Select(li => (string)li.Element("thing")).ToArray();
        Check(sources.Contains("DR_ColdSeep"), "cold seeps can be sampled");
        Check(sources.Contains("DR_UnderwaterVolcano"), "underwater volcanoes can be sampled");
        Check(sources.Contains("SteamGeyser"), "hydrothermal vents can be sampled");
        Check(comp.Element("sources").Elements("li").All(li => (float)li.Element("totalPoints") > 0f), "every site has a finite, positive research total");
        Check(def.Element("placeWorkers")?.Elements("li").Any(li => li.Value == "DepthRim.PlaceWorker_SeafloorSampler") == true, "placement requires an adjacent site");

        // 冷泉出現在升降井到得了的每個生態區（淺海、變溫層）
        int shaftBiomes = 0;
        foreach (XElement biome in all.Where(d => d.Name.LocalName == "BiomeDef"))
        {
            XElement ext = biome.Element("modExtensions")?.Elements("li").FirstOrDefault(li => (string)li.Attribute("Class") == "DepthRim.WaterMapExtension");
            string tier = (string)ext?.Element("depthTier");
            if (tier != "Shallow" && tier != "Thermocline") { continue; }
            shaftBiomes++;
            Check(ext.Element("seafloorFeatures")?.Elements("li").Any(li => (string)li.Element("thing") == "DR_ColdSeep") == true,
                $"{biome.Element("defName").Value}: cold seeps generate where an ice shaft can reach");
        }
        Check(shaftBiomes >= 3, $"found shaft-reachable biomes ({shaftBiomes})");

        // 研究：FFF 知識分頁
        XElement tab = Def("DepthRim_SeabedResearchTab");
        Check(tab?.Element("modExtensions")?.Elements("li").Any(li => (string)li.Attribute("Class") == "Fortified.ModExtension_UniqueResearchTab"
            && li.Element("categories").Elements("li").Any(c => c.Value == "DepthRim_Seabed")) == true, "the seabed research tab is an FFF knowledge tab with the seabed category");
        Check(Def("DepthRim_Seabed")?.Name.LocalName == "KnowledgeCategoryDef", "the seabed knowledge category exists");
        List<XElement> projects = all.Where(d => d.Name.LocalName == "ResearchProjectDef" && ((string)d.Element("defName")).StartsWith("DepthRim_")).ToList();
        foreach (XElement p in projects)
        {
            string name = (string)p.Element("defName");
            if (name == "DepthRim_UnderwaterSurvival")
            {
                Check((string)p.Element("tab") == "DepthRim_ResearchTab" && p.Element("baseCost") != null && p.Element("knowledgeCategory") == null,
                    "the starting tech stays ordinary research on the underwater tab");
                continue;
            }
            Check((string)p.Element("knowledgeCategory") == "DepthRim_Seabed" && (float?)p.Element("knowledgeCost") > 0f && p.Element("baseCost") == null,
                $"{name} costs seabed research");
            Check((string)p.Element("tab") == "DepthRim_SeabedResearchTab", $"{name} is on the seabed research tab");
        }
        Check(projects.Count >= 6, $"found the underwater research projects ({projects.Count})");
    }
}
