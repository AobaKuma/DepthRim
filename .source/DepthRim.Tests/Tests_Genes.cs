using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 海洋基因（Docs/基因_Biotech設計.md）：耐壓參數、暈船、鰓、畏光的純計算與 XML。
static partial class Test
{
    static void GenePressureMath()
    {
        const int day = PressureAdaptationMath.TicksPerDay;
        PressureProfile baseline = PressureProfile.Baseline;
        PressureProfile fragile = new PressureProfile { tolerance = 0.5f, speed = 0.5f, maxLevel = 2, damageFactor = 1.5f };
        PressureProfile adapted = new PressureProfile { tolerance = 1.5f, speed = 1.5f, maxLevel = 3, damageFactor = 0.75f };
        PressureProfile immune = new PressureProfile { tolerance = 3f, speed = 1f, maxLevel = 3, damageFactor = 0f };

        // 基準和原本的公式完全一樣
        Near(PressureAdaptationMath.DaysPerLevel(1f, 1f), PressureAdaptationMath.DaysPerLevel(1f), "speed 1 keeps the old days per level");
        Near(PressureAdaptationMath.DaysPerLevel(4f, 1f), 0.5f, "speed 1 keeps the old clamp");
        Near(PressureAdaptationMath.Step(0f, 2f, 1f, day, baseline), PressureAdaptationMath.Step(0f, 2f, 1f, day), "baseline profile steps like the old formula");
        Check(PressureAdaptationMath.TicksUntilComfortable(0f, 3f, 1f, baseline) == PressureAdaptationMath.TicksUntilComfortable(0f, 3f, 1f), "baseline wait is unchanged");

        // 適應速度
        Near(PressureAdaptationMath.DaysPerLevel(1f, 0.5f), 2f, "half speed: 2 days per level");
        Near(PressureAdaptationMath.DaysPerLevel(0.5f, 0.5f), 4f, "half speed with poor kidneys: 4 days (new upper bound)");
        Near(PressureAdaptationMath.DaysPerLevel(0.2f, 0.5f), 4f, "never slower than 4 days");
        Near(PressureAdaptationMath.DaysPerLevel(2f, 1.5f), 1f / 3f, "fast genes with good kidneys go below half a day");
        Near(PressureAdaptationMath.DaysPerLevel(4f, 4f), 0.25f, "never faster than a quarter day");
        Near(PressureAdaptationMath.DaysPerLevel(1f, 0f), 4f, "zero speed falls back to the slowest rate");

        // 容許差
        Near(PressureAdaptationMath.Mismatch(0f, 1f, 0.5f), 0.5f, "fragile: one level off already hurts");
        Near(PressureAdaptationMath.Mismatch(0f, 1.5f, 1.5f), 0f, "deep-adapted: 1.5 levels off is fine");
        Near(PressureAdaptationMath.Mismatch(0f, 3f, 3f), 0f, "abyssal body: surface to seabed is fine");
        Near(PressureAdaptationMath.Mismatch(0f, 2f, 1f), PressureAdaptationMath.Mismatch(0f, 2f), "tolerance 1 matches the old mismatch");

        // 可適應上限：不耐壓的人在海床只會適應到 2，永遠差 1 級（容許 0.5 → 0.5 的不適）
        float a = 0f;
        for (int t = 0; t < 20 * day; t += 250) { a = PressureAdaptationMath.Step(a, 3f, 1f, 250, fragile); }
        Near(a, 2f, "fragile body stops adapting at the deep-sea level");
        Near(PressureAdaptationMath.Mismatch(a, 3f, fragile.tolerance), 0.5f, "and stays mildly uncomfortable on the seabed");
        Check(PressureAdaptationMath.TicksUntilComfortable(a, 3f, 1f, fragile) == PressureAdaptationMath.Never, "the wait is reported as never");
        Check(PressureAdaptationMath.TicksUntilComfortable(0f, 2f, 1f, fragile) > 0, "the deep sea is reachable for a fragile body");
        Near(PressureAdaptationMath.Step(2.5f, 3f, 1f, day, fragile), 2f, "an adapted level above the cap drifts back to it");

        // 速度：深海適應 1 天內適應 1.5 級
        Near(PressureAdaptationMath.Step(0f, 3f, 1f, day, adapted), 1.5f, "deep-adapted: 1.5 levels a day");
        Check(PressureAdaptationMath.TicksUntilComfortable(0f, 3f, 1f, adapted) == (int)System.Math.Ceiling(1.5f / 1.5f * day), "deep-adapted: surface to seabed comfortable after a day");
        Check(PressureAdaptationMath.TicksUntilComfortable(0f, 3f, 1f, immune) == 0, "abyssal body: no wait");
        Check(immune.NeverUncomfortable && !adapted.NeverUncomfortable && !baseline.NeverUncomfortable, "only tolerance 3 is pressure immune");

        // 水密門：容許差跟著改
        Check(!PressureAdaptationMath.DoorAllows(false, 0f, 0f, 1f, 0.5f), "fragile colonist will not walk into a level-1 chamber");
        Check(PressureAdaptationMath.DoorAllows(false, 0f, 0f, 3f, 3f), "abyssal colonist walks anywhere");
        Check(PressureAdaptationMath.DoorAllows(false, 0f, 0f, 1f) == PressureAdaptationMath.DoorAllows(false, 0f, 0f, 1f, 1f), "tolerance 1 door rule is the old one");

        // 出航確認：有鰓的人不需要潛水服；容許差影響「不適應」
        Check(BoardingReadinessMath.Assess(true, false, 0f, 2f, 0f, DepthTier.Deep, 1.5f, true) == BoardingReadinessIssue.Maladapted, "gilled deep-adapted crew needs no suit for the deep (two levels down is still 0.5 past its tolerance)");
        Check(BoardingReadinessMath.Assess(true, false, 0f, 2f, 0.5f, DepthTier.Deep, 1.5f, true) == BoardingReadinessIssue.None, "half a level of adaptation makes it ready");
        Check(BoardingReadinessMath.Assess(true, false, 0f, 0f, 0f, DepthTier.Shallow, 1f, false) == BoardingReadinessIssue.NoSuit, "without gills a suit is still needed");
        Check(BoardingReadinessMath.Assess(true, true, 1f, 2f, 0f, DepthTier.Thermocline, 0.5f, false) == BoardingReadinessIssue.Maladapted, "fragile crew is maladapted one level down");
    }

    static void GeneBreathing()
    {
        // 溺水：倍率只乘在淨增量上，自然消退照舊抵掉
        const float per = 0.15f, rec = 0.05f;
        Near(FloodingMath.DrowningAdded(per, rec, 1f), per, "factor 1 adds the full amount");
        Near(FloodingMath.DrowningAdded(per, rec, 0f), 0f, "factor 0 (gills) adds nothing");
        Near(FloodingMath.DrowningAdded(per, rec, 0.35f) - rec, (per - rec) * 0.35f, "long breath: net drowning x0.35");
        Check(FloodingMath.DrowningAdded(per, rec, 0.35f) > rec, "long breath still drowns, only slower");

        // 氣瓶耗氣倍率
        var air = new CompDivingAir { props = new CompProperties_DivingAir { capacityTicks = 1000 } };
        air.TryConsume(100, 0.6f);
        Near(air.Air, 940f, "air use x0.6");
        air.TryConsume(100, 0f);
        Near(air.Air, 940f, "air use x0 consumes nothing");
        air.TryConsume(100);
        Near(air.Air, 840f, "the old overload still uses rate 1");

        // 鰓乾燥
        const int day = PressureAdaptationMath.TicksPerDay;
        Near(MarineGeneMath.GillDrying(0f, false, 20f, 3 * day), 0.5f, "three days dry: parched (0.5)");
        Near(MarineGeneMath.GillDrying(0f, false, 35f, 2 * day), 0.5f, "in the heat it dries 1.5x faster");
        Near(MarineGeneMath.GillDrying(0.8f, true, 20f, 250), 0f, "a soak resets it at once");
        Near(MarineGeneMath.GillDrying(0.99f, false, 40f, 10 * day), MarineGeneMath.GillDryingMax, "capped");
        Check(MarineGeneMath.GillWetLevel >= 0.25f && MarineGeneMath.GillWetLevel < ConditionalStatAffecter_Submerged.Level, "waist-deep water wets the gills, no need to be submerged");

        // 畏光門檻：原版人造光最多 0.5
        Check(!MarineGeneMath.Glaring(0, 1f), "no gene: never glaring");
        Check(!MarineGeneMath.Glaring(1, 0.5f), "photophobic: an ordinary lamp (0.5) is fine");
        Check(MarineGeneMath.Glaring(1, 1f), "photophobic: daylight and overlight glare");
        Check(MarineGeneMath.Glaring(2, 0.5f), "light-blind: an ordinary lamp glares");
        Check(!MarineGeneMath.Glaring(2, 0.2f), "light-blind: dim places are fine");
        Check(MarineGeneMath.Dark(0.1f) && !MarineGeneMath.Dark(0.5f), "dark threshold");
    }

    static void SeasicknessMathTests()
    {
        const int day = PressureAdaptationMath.TicksPerDay;
        Near(SeasicknessMath.SeaExposure(1f), 0f, "calm sea: no exposure");
        Near(SeasicknessMath.SeaExposure(1.3f), 0f, "threshold: none");
        Near(SeasicknessMath.SeaExposure(2.5f), 1f, "sea storm: full exposure");
        Check(SeasicknessMath.SeaExposure(2f) > 0.5f && SeasicknessMath.SeaExposure(2f) < 0.65f, "rough sea: a bit over half");
        Near(SeasicknessMath.VoyageExposure(false, false), 0f, "steady voyage at depth: none");
        Near(SeasicknessMath.VoyageExposure(true, false), SeasicknessMath.DivingExposure, "diving");
        Near(SeasicknessMath.VoyageExposure(true, true), SeasicknessMath.DivingExposure + SeasicknessMath.ShallowVoyageExposure, "diving in the shallows adds up");

        // 平衡：基準在暴風雨到「暈船」、惡劣海況只到「頭暈」；易暈船到「嚴重」；海腳不暈
        float storm = SeasicknessMath.Target(SeasicknessMath.SeaExposure(2.5f), 1f, false);
        float rough = SeasicknessMath.Target(SeasicknessMath.SeaExposure(2f), 1f, false);
        Check(storm >= SeasicknessMath.SeasickSeverity && storm < SeasicknessMath.SevereSeverity, $"baseline in a storm: seasick ({storm:F2})");
        Check(rough >= SeasicknessMath.QueasySeverity && rough < SeasicknessMath.SeasickSeverity, $"baseline in rough sea: queasy ({rough:F2})");
        Check(SeasicknessMath.Target(1f, 2f, false) >= SeasicknessMath.SevereSeverity, "seasick-prone in a storm: severe");
        Near(SeasicknessMath.Target(1f, 0f, false), 0f, "sea legs: none");
        Check(SeasicknessMath.Target(SeasicknessMath.DivingExposure, 1f, false) >= SeasicknessMath.QueasySeverity, "a dive makes baseliners queasy");
        Check(SeasicknessMath.Target(1f, 1f, true) < storm, "acclimated: less");

        // 速度
        Near(SeasicknessMath.Step(0f, 0.6f, day / 10, false), 0.2f, "rises 2 per day");
        Near(SeasicknessMath.Step(0f, 0.6f, day / 10, true), 0.1f, "half as fast asleep");
        Near(SeasicknessMath.Step(0.6f, 0f, day / 10, false), 0.2f, "falls 4 per day");
        Near(SeasicknessMath.Step(0.1f, 0.6f, day, false), 0.6f, "does not overshoot the target");
        Near(SeasicknessMath.Step(0.6f, 0f, day, false), 0f, "recovers within hours");

        // 習慣
        int exposed = 0, away = 0;
        SeasicknessMath.Track(ref exposed, ref away, true, SeasicknessMath.AcclimateTicks);
        Check(SeasicknessMath.Acclimated(exposed, true), "three days at sea: used to it");
        Check(!SeasicknessMath.Acclimated(exposed, false), "seasick-prone never gets used to it");
        SeasicknessMath.Track(ref exposed, ref away, false, day);
        Check(SeasicknessMath.Acclimated(exposed, true), "a day ashore keeps the habit");
        SeasicknessMath.Track(ref exposed, ref away, false, SeasicknessMath.ForgetTicks);
        Check(exposed == 0 && !SeasicknessMath.Acclimated(exposed, true), "five days ashore forget it");
    }

    static void ContentGenes()
    {
        string dir = Path.Combine(ModRoot, "1.7", "Defs", "Genes");
        XDocument genes = XDocument.Load(Path.Combine(dir, "Genes_Marine.xml"));
        XDocument xenos = XDocument.Load(Path.Combine(dir, "Xenotypes_Marine.xml"));
        XDocument effects = XDocument.Load(Path.Combine(dir, "Genes_Effects.xml"));
        var defs = genes.Root.Elements("GeneDef").Where(e => e.Element("defName") != null).ToList();
        var byName = defs.ToDictionary(e => (string)e.Element("defName"));
        IEnumerable<string> Tags(XElement g) => g.Element("exclusionTags")?.Elements("li").Select(l => l.Value) ?? Enumerable.Empty<string>();
        XElement Ext(XElement g) => g.Element("modExtensions")?.Elements("li").FirstOrDefault(l => (string)l.Attribute("Class") == "DepthRim.GeneExtension_DepthRim");

        Check(defs.Count >= 25, $"marine genes defined ({defs.Count})");
        Check(defs.All(g => g.Element("label") != null && g.Element("description") != null), "every gene has a label and description");
        Check(defs.All(g => g.Element("defName").Value.StartsWith("DepthRim_")), "gene defNames are prefixed");

        // 四級耐壓：同一個互斥標籤；抗性 0/1/2/3；只有淵體是 archite
        string[] tiers = { "DepthRim_PressureFragile", "DepthRim_PressureTolerant", "DepthRim_PressureAdapted", "DepthRim_PressureImmune" };
        Check(tiers.All(t => byName.ContainsKey(t) && Tags(byName[t]).Contains("DepthRim_PressureTolerance")), "four pressure genes share one exclusion tag");
        for (int i = 0; i < tiers.Length; i++)
        {
            string r = (string)byName[tiers[i]].Element("statOffsets")?.Element("DepthRim_PressureResistance");
            Check((r == null ? 0 : int.Parse(r)) == i, $"{tiers[i]} gives pressure resistance {i}");
            Check(Ext(byName[tiers[i]])?.Element("pressureTolerance") != null, $"{tiers[i]} sets a tolerance");
        }
        Check((string)byName["DepthRim_PressureImmune"].Element("biostatArc") == "1", "abyssal body is archite");
        Check(defs.Where(g => (string)g.Element("biostatArc") == "1").Count() == 1, "it is the only archite marine gene");
        Check(int.Parse((string)byName["DepthRim_PressureFragile"].Element("biostatMet")) > 0, "pressure-fragile is a negative gene (gives metabolism)");

        // 互斥組
        Check(Tags(byName["DepthRim_Gills"]).Contains("DepthRim_WaterBreathing") && Tags(byName["DepthRim_LongBreath"]).Contains("DepthRim_WaterBreathing"), "gills and long breath exclude each other");
        Check(Tags(byName["DepthRim_SeasickProne"]).Contains("DepthRim_Seasickness") && Tags(byName["DepthRim_SeaLegs"]).Contains("DepthRim_Seasickness"), "seasick-prone and sea legs exclude each other");
        Check(Tags(byName["DepthRim_Photophobia"]).Contains("DepthRim_LightSensitivity") && Tags(byName["DepthRim_PhotophobiaIntense"]).Contains("DepthRim_LightSensitivity"), "the two photophobia genes exclude each other");

        // 需要定時結算的基因有自己的 geneClass
        foreach (string g in new[] { "DepthRim_Gills", "DepthRim_PhotophobiaIntense", "DepthRim_DecompressionResistant" })
        {
            Check((string)byName[g].Element("geneClass") == "DepthRim.Gene_DepthRim", $"{g} uses Gene_DepthRim");
        }
        Check((string)byName["DepthRim_Bioluminescent"].Element("geneClass") == "DepthRim.Gene_Bioluminescent", "bioluminescent uses its own gene class");

        // 不依賴 Biotech：displayCategory 是自己的分類，或加 MayRequire；不繼承 Biotech 的抽象 Def
        var abstracts = genes.Root.Elements("GeneDef").Where(e => (string)e.Attribute("Abstract") == "True").Select(e => (string)e.Attribute("Name")).ToHashSet();
        Check(defs.All(g => g.Attribute("ParentName") == null || abstracts.Contains((string)g.Attribute("ParentName"))), "genes only inherit this file's abstract defs (Biotech's may be missing)");
        foreach (XElement cat in genes.Root.Descendants("displayCategory"))
        {
            Check(cat.Value == "DepthRim_Marine" || cat.Attribute("MayRequire") != null, $"display category {cat.Value} is ours or MayRequire");
        }
        Check(genes.Root.Elements("GeneCategoryDef").Any(c => (string)c.Element("defName") == "DepthRim_Marine"), "the marine gene category exists");

        // 種族：引用的基因都存在（原版的加 MayRequire）
        foreach (XElement x in xenos.Root.Elements("XenotypeDef"))
        {
            string name = (string)x.Element("defName");
            foreach (XElement li in x.Element("genes").Elements("li"))
            {
                Check(byName.ContainsKey(li.Value) || li.Attribute("MayRequire") != null, $"{name}: gene {li.Value} exists or is MayRequire");
            }
            var gs = x.Element("genes").Elements("li").Select(l => l.Value).Where(byName.ContainsKey).ToList();
            foreach (var group in gs.SelectMany(g => Tags(byName[g]).Select(t => (t, g))).GroupBy(p => p.t))
            {
                Check(group.Count() == 1, $"{name}: no two genes share exclusion tag {group.Key}");
            }
        }
        Check(xenos.Root.Elements("XenotypeDef").Select(x => (string)x.Element("defName")).SequenceEqual(new[] { "DepthRim_Mariner", "DepthRim_Deepdiver", "DepthRim_Abyssal" }), "three marine xenotypes");
        Check((string)xenos.Root.Elements("XenotypeDef").Single(x => (string)x.Element("defName") == "DepthRim_Abyssal").Element("inheritable") == "false", "abyssals are archite: not inheritable");

        // 暈船 hediff 與想法：階段對得上
        XElement sick = effects.Root.Elements("HediffDef").Single(e => (string)e.Element("defName") == "DepthRim_Seasick");
        var sickStages = sick.Element("stages").Elements("li").Select(s => float.Parse((string)s.Element("minSeverity"))).ToList();
        Check(sickStages.SequenceEqual(new[] { 0f, SeasicknessMath.QueasySeverity, SeasicknessMath.SeasickSeverity, SeasicknessMath.SevereSeverity }), "seasick hediff stages match SeasicknessMath");
        Check((string)sick.Element("hediffClass") == "DepthRim.Hediff_Seasick", "seasick hediff class");
        Check(sick.Element("lethalSeverity") == null, "seasickness is never lethal");
        XElement thought = effects.Root.Elements("ThoughtDef").Single(e => (string)e.Element("defName") == "DepthRim_Seasick");
        Check(thought.Element("stages").Elements("li").Count() == sickStages.Count - 1, "one seasick thought stage per visible hediff stage");
        Check(effects.Root.Elements("HediffDef").Single(e => (string)e.Element("defName") == "DepthRim_GillDrying").Element("lethalSeverity") == null, "dry gills are never lethal");

        // 補丁：防水皮膚抵銷濕透；Odyssey 的 breathless 算屏息
        string patches = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Patches", "Genes_Patches.xml"));
        Check(patches.Contains("SoakingWet") && patches.Contains("DepthRim_Waterproof"), "waterproof skin nullifies soaking wet");
        Check(patches.Contains("VacuumResistance_Total") && patches.Contains("<drowningFactor>0.35</drowningFactor>"), "Odyssey breathless counts as long breath underwater");
    }
}
