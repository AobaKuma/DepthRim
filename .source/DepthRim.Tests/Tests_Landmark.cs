using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 聲納地標（2026-11-18，Docs/聲納地標與穿梭艇探索_機制設計.md）：技能判定、聲納發現頻率，以及地標定義的內容一致性。
static partial class Test
{
    static void LandmarkMathTests()
    {
        // 成功率：技能等於難度時是基本率；每級 6%；夾在 5%～95%
        Near(LandmarkMath.Chance(0.5f, 8, 8), 0.5f, "skill equal to difficulty gives the base chance");
        Near(LandmarkMath.Chance(0.5f, 13, 8), 0.8f, "five levels above difficulty adds 30%");
        Near(LandmarkMath.Chance(0.5f, 3, 8), 0.2f, "five levels below difficulty takes 30%");
        Near(LandmarkMath.Chance(0.5f, 20, 0), LandmarkMath.MaxChance, "chance is capped at 95%");
        Near(LandmarkMath.Chance(0.5f, 0, 20), LandmarkMath.MinChance, "chance never drops below 5%");
        Near(LandmarkMath.Chance(0.5f, 8, 8, 0.5f), 0.25f, "a half-capable actor halves the chance");
        Near(LandmarkMath.Chance(0.5f, 8, 8, 0f), 0.1f, "capability counts as at least 20%");
        Near(LandmarkMath.Chance(0.5f, 8, 8, 1.4f), 0.5f, "capability above 100% does not raise the chance");

        // 擲骰：低於成功率的四分之一是大成功
        Check(LandmarkMath.Resolve(0.8f, 0.1f) == LandmarkResult.Critical, "a roll under a quarter of the chance is a great success");
        Check(LandmarkMath.Resolve(0.8f, 0.5f) == LandmarkResult.Success, "a roll under the chance succeeds");
        Check(LandmarkMath.Resolve(0.8f, 0.85f) == LandmarkResult.Failure, "a roll over the chance fails");
        Check(LandmarkMath.Resolve(0.8f, 0.2f) == LandmarkResult.Success, "exactly a quarter is not a great success");
        Near(LandmarkMath.Xp(600f, LandmarkResult.Failure), 300f, "failing still teaches half the xp");
        Near(LandmarkMath.Xp(600f, LandmarkResult.Critical), 600f, "success teaches the full xp");

        // 發現：範圍越大越常發現；沒有範圍就不會
        Near(LandmarkMath.DiscoveryChancePerHour(0f), 0f, "no sonar range finds nothing");
        Check(LandmarkMath.DiscoveryChancePerHour(6f) > LandmarkMath.DiscoveryChancePerHour(3f), "active sonar (6 tiles) finds landmarks more often than passive (3)");
        Near(LandmarkMath.DiscoveryChancePerHour(6f) * 24f, 0.48f, "active sonar finds about one landmark every two days");
        Near(LandmarkMath.DiscoveryChancePerHour(6f) / LandmarkMath.DiscoveryChancePerHour(3f), 2f, "the two sonars differ only by range");
        Check(LandmarkMath.CanDiscover(0, 0), "an empty sea can get landmarks");
        Check(!LandmarkMath.CanDiscover(LandmarkMath.MaxPerSonar, 0), "a sonar stops finding landmarks at its cap");
        Check(!LandmarkMath.CanDiscover(0, LandmarkMath.MaxInWorld), "the world stops getting landmarks at its cap");
    }

    static void ContentLandmark()
    {
        List<XElement> all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        XElement Def(string name) => all.FirstOrDefault(d => (string)d.Element("defName") == name);
        List<XElement> landmarks = all.Where(d => d.Name.LocalName == "DepthRim.OceanLandmarkDef").ToList();
        Check(landmarks.Count >= 4, $"found the first-version landmarks ({landmarks.Count})");
        HashSet<string> skills = new HashSet<string> { "Shooting", "Melee", "Construction", "Mining", "Cooking", "Plants", "Animals", "Crafting", "Artistic", "Medicine", "Social", "Intellectual" };
        HashSet<string> tiers = new HashSet<string>(System.Enum.GetNames(typeof(DepthTier)));
        int encounters = 0, sites = 0;
        foreach (XElement l in landmarks)
        {
            string name = (string)l.Element("defName");
            bool site = (string)l.Element("kind") == "Site";
            XElement wo = Def((string)l.Element("worldObjectDef"));
            if (!Check(wo != null && wo.Name.LocalName == "WorldObjectDef", $"{name}: its world object exists")) { continue; }
            string cls = (string)wo.Element("worldObjectClass");
            Check(cls == (site ? "DepthRim.WorldObject_LandmarkSite" : "DepthRim.WorldObject_OceanLandmark"), $"{name}: world object class fits its kind ({cls})");
            Check(l.Element("depthTiers")?.Elements("li").All(li => tiers.Contains(li.Value)) != false, $"{name}: depth tiers are valid");
            if (site)
            {
                sites++;
                XElement gen = Def((string)wo.Element("mapGenerator"));
                Check(gen != null && (string)gen.Attribute("ParentName") == "DepthRim_OceanBase", $"{name}: the site map is an ocean map");
                Check(l.Element("depthTiers").Elements("li").All(li => li.Value != "Deep"), $"{name}: sites only appear where there is a seafloor to land on");
                continue;
            }
            encounters++;
            List<XElement> options = l.Element("options")?.Elements("li").ToList() ?? new List<XElement>();
            Check(options.Count >= 2, $"{name}: has at least two options besides leaving");
            foreach (XElement o in options)
            {
                string skill = (string)o.Element("skill");
                Check(skills.Contains(skill), $"{name}: option skill {skill} exists");
                Check(o.Element("successText") != null && o.Element("failureText") != null, $"{name}: option '{(string)o.Element("label")}' has success and failure text");
                Check(o.Element("success")?.Elements("li").Any() == true, $"{name}: option '{(string)o.Element("label")}' gives something on success");
                foreach (XElement outcome in o.Descendants("li").Where(li => li.Attribute("Class") != null))
                {
                    string oc = (string)outcome.Attribute("Class");
                    Check(DepthRimAssembly.GetType(oc) != null, $"{name}: outcome {oc} exists");
                    if (oc == "DepthRim.LandmarkOutcome_Incident") { Check(Def((string)outcome.Element("incident")) != null, $"{name}: incident {(string)outcome.Element("incident")} exists"); }
                    if (oc == "DepthRim.LandmarkOutcome_Knowledge") { Check((string)outcome.Element("category") == "DepthRim_Seabed", $"{name}: knowledge goes to seabed research"); }
                }
            }
        }
        Check(encounters >= 3 && sites >= 1, $"first version has 3 encounters and a site ({encounters}, {sites})");
        XElement step = Def("DR_Shipwreck");
        Check((string)step?.Element("genStep")?.Attribute("Class") == "DepthRim.GenStep_Shipwreck", "the shipwreck gen step exists");
        Check((int?)step?.Element("order") < 1500, "the wreck is built before the fog is revealed");

        // 繁中翻譯：每個地標的名稱、說明、每個選項的文字都有
        string zh = Directory.GetDirectories(Path.Combine(ModRoot, "1.7", "Languages")).First(d => Path.GetFileName(d).StartsWith("ChineseTraditional"));
        string file = Path.Combine(zh, "DefInjected", "DepthRim.OceanLandmarkDef", "DepthRim_Landmarks.xml");
        if (!Check(File.Exists(file), "Traditional Chinese landmark DefInjected exists")) { return; }
        HashSet<string> keys = new HashSet<string>(XDocument.Load(file).Root.Elements().Select(e => e.Name.LocalName));
        foreach (XElement l in landmarks)
        {
            string name = (string)l.Element("defName");
            Check(keys.Contains(name + ".label") && keys.Contains(name + ".description"), $"{name}: label and description translated");
            int i = 0;
            foreach (XElement o in l.Element("options")?.Elements("li") ?? Enumerable.Empty<XElement>())
            {
                foreach (string field in new[] { "label", "criticalText", "successText", "failureText" })
                {
                    if (o.Element(field) != null) { Check(keys.Contains($"{name}.options.{i}.{field}"), $"{name}: option {i} {field} translated"); }
                }
                i++;
            }
        }
    }
}

// 玩家突擊艇的燃料與故障（2026-11-18）：每格 5 化學燃料，要有來回的量才能出發；滿箱剛好來回航程上限。
static partial class Test
{
    static void BoardingCraftFuelTests()
    {
        Near(BoardingCraftFuel.OneWay(3), 15f, "three tiles one way cost 15 chemfuel");
        Near(BoardingCraftFuel.RoundTrip(3), 30f, "the round trip costs twice the one way");
        Check(BoardingCraftFuel.CanReach(30f, 3) && !BoardingCraftFuel.CanReach(29f, 3), "launching needs fuel for the whole round trip");
        Check(BoardingCraftFuel.RoundTripTiles(80f) == Building_BoardingCraft.RangeTiles, "a full tank covers exactly the craft's range there and back");
        Check(BoardingCraftFuel.RoundTripTiles(9f) == 0, "less than one round-trip tile is no range");
        Near(BoardingCraftFuel.OneWay(-2), 0f, "no distance burns nothing");

        List<XElement> all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        XElement craft = all.FirstOrDefault(d => (string)d.Element("defName") == "DepthRim_PlayerBoardingCraft");
        if (!Check(craft != null, "the player boarding craft exists")) { return; }
        List<XElement> comps = craft.Element("comps")?.Elements("li").ToList() ?? new List<XElement>();
        XElement fuel = comps.FirstOrDefault(c => (string)c.Attribute("Class") == "CompProperties_Refuelable");
        Check(fuel != null, "the player craft takes fuel");
        Check((float?)fuel?.Element("fuelCapacity") == BoardingCraftFuel.RoundTrip(Building_BoardingCraft.RangeTiles), "a full tank is one maximum round trip");
        Check(fuel?.Element("fuelFilter")?.Descendants("li").Any(li => li.Value == "Chemfuel") == true, "the craft burns chemfuel");
        Check((float?)fuel?.Element("fuelConsumptionRate") == 0f && (string)fuel?.Element("consumeFuelOnlyWhenUsed") == "true", "fuel is only burnt by trips, not while parked");
        Check(comps.Any(c => (string)c.Attribute("Class") == "CompProperties_Breakdownable"), "the player craft can break down");
        XElement enemy = all.FirstOrDefault(d => (string)d.Element("defName") == "DepthRim_BoardingCraft");
        Check(enemy?.Element("comps")?.Elements("li").Any(c => ((string)c.Attribute("Class"))?.Contains("Refuelable") == true) != true, "raiders' craft needs no fuel");
    }
}
