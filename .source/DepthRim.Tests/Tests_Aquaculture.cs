using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;
using UnityEngine;

// 箱網養殖（Docs/箱網養殖_內容設計.md §15.1）：水溫、溶氧、成長、飼料、死亡、收成、氣候與洋流、病害、魚影；品種與 Def 的內容檢查。
static partial class Test
{
    static void AquacultureMath()
    {
        // ── 灣內水溫 ──
        Near(NetCageMath.BayWaterTemperature(10f, 10f), 12.0f, "bay: annual 10 °C air -> 12.0 °C water");
        Near(NetCageMath.BayWaterTemperature(10f, 20f), 17.5f, "bay: summer 20 °C -> 17.5 °C");
        Near(NetCageMath.BayWaterTemperature(10f, 0f), 6.5f, "bay: winter 0 °C -> 6.5 °C");
        Near(NetCageMath.BayWaterTemperature(-30f, -50f), -1.8f, "bay: clamped at freezing");
        Near(NetCageMath.BayWaterTemperature(40f, 50f), 29.5f, "bay: clamped at 29.5 °C");

        // ── 外海水溫 ──
        Near(NetCageMath.OpenSeaDamping(10f), 0.474f, "open sea damping at 10 m ~0.47", 0.01f);
        Near(NetCageMath.OpenSeaDamping(150f), 0.069f, "open sea damping at 150 m ~0.07", 0.005f);
        Check(NetCageMath.OpenSeaDamping(500f) < 0.002f, "open sea damping at 500 m ~0");
        float t10 = NetCageMath.OpenSeaWaterTemperature(10f, 10f, 10f, OceanTemperature.Kind.OpenWater);
        float t150 = NetCageMath.OpenSeaWaterTemperature(150f, 10f, 10f, OceanTemperature.Kind.OpenWater);
        float t500 = NetCageMath.OpenSeaWaterTemperature(500f, 10f, 10f, OceanTemperature.Kind.OpenWater);
        Near(t10, 12.0f, "temperate surface net 12.0 °C", 0.1f);
        Near(t150, 9.4f, "temperate thermocline net 9.4 °C", 0.15f);
        Near(t500, 4.6f, "temperate deep net 4.6 °C", 0.15f);
        Near(NetCageMath.OpenSeaWaterTemperature(10f, 28f, 28f, OceanTemperature.Kind.OpenWater), 26.4f, "tropical surface 26.4 °C", 0.15f);
        Near(NetCageMath.OpenSeaWaterTemperature(10f, -10f, -10f, OceanTemperature.Kind.OpenWater), -1.8f, "polar surface at freezing", 0.05f);
        Near(NetCageMath.OpenSeaWaterTemperature(150f, 10f, 10f, OceanTemperature.Kind.Hydrothermal) - t150, 6f, "hydrothermal +6 °C", 0.01f);
        Near(NetCageMath.OpenSeaWaterTemperature(10f, -10f, 5f, OceanTemperature.Kind.Subglacial), NetCageMath.OpenSeaWaterTemperature(10f, -10f, -20f, OceanTemperature.Kind.Subglacial), "subglacial water has no season");
        Check(NetCageMath.OpenSeaWaterTemperature(10f, 10f, 20f, OceanTemperature.Kind.OpenWater) - t10 > 4.5f, "surface swings ~±4.7 °C with ±10 °C air");

        // ── 溶氧（§5.2 的表）──
        Near(NetCageMath.Oxygen(10f, 20f, 300f, 1.0f, 0, 0, false), 0.65f, "surface cod full, 20 °C: 0.65", 0.01f);
        Near(NetCageMath.Oxygen(150f, 10f, 300f, 1.2f, 0, 0, false), 0.59f, "thermocline salmon full, 10 °C: 0.59", 0.01f);
        Near(NetCageMath.Oxygen(500f, 4f, 300f, 0.9f, 0, 0, false), 0.53f, "deep frostfish full, 4 °C: 0.53", 0.01f);
        Near(NetCageMath.Oxygen(10f, 26f, 300f, 1.6f, 0, 0, false), 0.40f, "tropical tuna full, 26 °C: 0.40", 0.01f);
        Near(NetCageMath.Oxygen(10f, 26f, 300f, 1.6f, 0, 1, false), 0.55f, "plus one aerator: 0.55", 0.01f);
        Near(NetCageMath.Oxygen(10f, 20f, 300f, 1.0f, 2, 0, false), 0.49f, "three cages together: 0.49", 0.01f);
        Near(NetCageMath.NeighborsPenalty(10), 0.24f, "neighbour penalty capped at 0.24");
        Near(NetCageMath.Aeration(5), 0.30f, "at most two aerators count");
        Near(NetCageMath.Oxygen(10f, 10f, 0f, 1f, 0, 0, true) - NetCageMath.Oxygen(10f, 10f, 0f, 1f, 0, 0, false), -0.18f, "algal bloom -0.18");
        Near(NetCageMath.Oxygen(10f, 10f, 100f, 1f, 0, 0, false, -NetCageMath.BayWeatherPenalty) - NetCageMath.Oxygen(10f, 10f, 100f, 1f, 0, 0, false), -0.08f, "bay rain -0.08");
        Near(NetCageMath.BaseOxygen(10f), 0.946f, "bay base oxygen 0.946", 0.001f);
        Check(NetCageMath.BayRoughWeather(1.5f, 0f) && NetCageMath.BayRoughWeather(1f, 1f) && !NetCageMath.BayRoughWeather(1f, 0.2f), "bay rough weather: strong wind or heavy rain");

        // ── 成長 ──
        float perDay = NetCageMath.GrowthPerDay(2.5f, 18f, 0f, 0.9f, false, 0.5f, true, 1f);
        Near(0.05f + perDay * 18f, 2.5f, "cod in ideal water reaches market weight in 18 days", 0.01f);
        Near(NetCageMath.GrowthPerDay(2.5f, 18f, 3f, 0.9f, false, 0.5f, true, 1f) / perDay, 0.5f, "3 °C out of range: half speed");
        Near(NetCageMath.GrowthPerDay(2.5f, 18f, 0f, 0.9f, false, 0.5f, false, 1f), 0f, "no feed: no growth");
        Near(NetCageMath.DensityFactor(1.5f), 0.6f, "density 1.5: growth x0.6");
        Near(NetCageMath.DensityFactor(0.8f), 1f, "density 0.8: no penalty");
        Near(NetCageMath.GrowthPerDay(2.5f, 18f, 0f, 0.9f, false, 0.5f, true, NetCageMath.BayStormGrowth) / perDay, 0.8f, "bay rain: x0.8");
        Near(NetCageMath.O2Factor(0.9f, sustainedLow: true), 0f, "sustained low oxygen stops growth");
        Near(NetCageMath.O2Factor(0.25f), 0.2f, "o2 0.25 -> x0.2");

        // ── 飼料：一個週期 240～360 個（藍魚密度低一點）──
        float CycleFeed(float stock, float hw, float days, float rate)
        {
            float total = 0f, w = 0.02f * hw;
            float g = NetCageMath.GrowthPerDay(hw, days, 0f, 0.9f, false, 0.5f, true, 1f);
            for (int d = 0; d < days * 10; d++) { total += NetCageMath.DailyFeed(stock * w, rate, 1f) * 0.1f; w = Mathf.Min(hw, w + g * 0.1f); }
            return total;
        }
        float cod = CycleFeed(120, 2.5f, 18, 0.09f);
        float tuna = CycleFeed(24, 12f, 30, 0.08f);
        Check(cod >= 230f && cod <= 360f && tuna >= 240f && tuna <= 360f, $"one cycle eats 240~360 kibble (cod {cod:F0}, tuna {tuna:F0})");
        Near(NetCageMath.DailyFeed(300f, 0.09f, 0f), 0f, "feed rate 0: no kibble eaten");

        // ── 死亡 ──
        Near(NetCageMath.DailyMortality(0.8f, 0.5f, 0f, 0f), 0.0015f, "base mortality 0.15%/day");
        Near(NetCageMath.DailyMortality(0f, 0.5f, 0f, 0f), 0.0215f, "no oxygen +2%");
        Near(NetCageMath.DailyMortality(0.8f, 1.0f, 0f, 0f), 0.0035f, "crowded +0.2%");
        Near(NetCageMath.DailyMortality(0.8f, 0.5f, 12f, 0f), 0.0115f, "12 °C out of range +1%");
        Near(NetCageMath.DailyMortality(0.8f, 0.5f, 0f, 1f), 0.0035f, "overripe +0.2%");
        Near(NetCageMath.OverripeDays(2f), 0f, "no overripeness in the first 3 days");

        // ── 收成 ──
        Check(NetCageMath.HarvestYield(120, 3f, 1f, 0f) == 360, "normal harvest: 120 cod x3");
        Check(NetCageMath.HarvestYield(120, 3f, 0.6f, 0f) == 240, "emergency at 60%: round(3x0.6)=2 each");
        Check(NetCageMath.HarvestYield(120, 1f, 0.5f, 0f) == 120, "emergency: at least 1 each");
        Check(NetCageMath.HarvestYield(120, 3f, 0.4f, 0f) == 0, "below 50% weight: nothing");
        Check(NetCageMath.HarvestYield(100, 3f, 1f, 2f) == 240, "overripe 2 days: x0.8 count");
        Check(NetCageMath.HarvestYield(100, 3f, 1f, 9f) == 150, "overripe floor 50%");

        // ── 破網 ──
        Near(NetCageMath.StormBreachChance(0.6f), 0f, "healthy net: no storm breach");
        Near(NetCageMath.StormBreachChance(0.45f), 0.05f, "damaged: 5% per storm hit");
        Near(NetCageMath.StormBreachChance(0.1f), 0.15f, "dangerous: 15%");
        Near(NetCageMath.DailyBreachChance(0.4f, CageVenue.Bay), 0.01f, "bay: 1% a day below 50%");
        Near(NetCageMath.DailyBreachChance(0.4f, CageVenue.OpenSea), 0.02f, "open sea: 2% a day below 50%");
    }

    static void AquacultureClimate()
    {
        ClimateEffect none = ClimateMath.Vanilla(false, false, false, false);
        Near(none.oxygen, 0f, "no events: no change");
        Near(ClimateMath.HeatWave.oxygen, -0.10f, "heat wave O2 -0.10");
        Near(ClimateMath.HeatWave.bloomMtbFactor, 0.6f, "heat wave bloom x0.6");
        Near(ClimateMath.ColdSnap.oxygen, 0.05f, "cold snap O2 +0.05");
        Near(ClimateMath.VolcanicWinter.growth, 0.85f, "volcanic winter growth x0.85");
        Near(ClimateMath.ToxicFallout.oxygen, -0.05f, "toxic fallout O2 -0.05");
        ClimateEffect both = ClimateMath.Vanilla(false, false, true, true);
        Near(both.growth, 0.85f * 0.85f, "events stack");
        // 熱浪寒流不改水溫（ClimateEffect 沒有水溫欄位）
        Check(typeof(ClimateEffect).GetField("waterTemp") == null, "vanilla events add no water temperature offset");

        Near(ClimateMath.CurrentOffset(OceanCurrentKind.Warm, 10f, CageVenue.OpenSea), 3f, "warm current +3 at the surface");
        Near(ClimateMath.CurrentOffset(OceanCurrentKind.Warm, 150f, CageVenue.OpenSea), 1.4f, "+1.4 at 150 m");
        Near(ClimateMath.CurrentOffset(OceanCurrentKind.Cold, 150f, CageVenue.OpenSea), -1.4f, "cold -1.4 at 150 m");
        Near(ClimateMath.CurrentOffset(OceanCurrentKind.Warm, 500f, CageVenue.OpenSea), 0f, "nothing at 500 m");
        Near(ClimateMath.CurrentOffset(OceanCurrentKind.Warm, 10f, CageVenue.Bay), 0f, "no currents in the bay");
        Near(ClimateMath.Current(OceanCurrentKind.Cold, CageVenue.Bay).oxygen, 0f, "bay: no current oxygen change");
        Near(ClimateMath.EventFactor(OceanCurrentKind.Warm), 1.15f, "current turbines x1.15 during a current");
        Near(ClimateMath.EventFactor(OceanCurrentKind.None), 1f, "otherwise x1");
        Check(ClimateMath.IsWarmWaterFish(10f, 32f) && !ClimateMath.IsWarmWaterFish(-2f, 12f), "bluefish is warm-water, cod is not");
        Check(ClimateMath.IsColdWaterFish(-2f, 12f) && !ClimateMath.IsColdWaterFish(10f, 32f), "cod is cold-water");
        Near(ClimateMath.CurrentGrowth(OceanCurrentKind.Cold, CageVenue.OpenSea, -2f, 12f), 1.1f, "cold current: cold-water fish x1.1");
        Near(ClimateMath.CurrentGrowth(OceanCurrentKind.Warm, CageVenue.OpenSea, -2f, 12f), 1f, "warm current: no bonus for cod");
        Near(OceanCurrentMath.Strength(DepthTier.Deep, 0f, 0, 0f, 99f, 1.15f) / OceanCurrentMath.Strength(DepthTier.Deep, 0f, 0, 0f, 99f), 1.15f, "turbine strength takes the event factor");
    }

    static void AquacultureDisease()
    {
        Near(DiseaseSpread.Crowding(0), 0f, "no neighbours: no spread");
        Near(DiseaseSpread.Crowding(1), 1f, "one neighbour x1");
        Near(DiseaseSpread.Crowding(4), 1.75f, "four neighbours x1.75");
        Near(DiseaseSpread.Crowding(9), 2f, "capped at x2");
        Near(DiseaseSpread.SpreadChance(1f, 1, CageVenue.OpenSea), 0.15f, "15% x severity x crowding");
        Near(DiseaseSpread.SpreadChance(1f, 1, CageVenue.Bay), 0.195f, "bay x1.3");
        Near(DiseaseSpread.SpreadChance(0.5f, 0, CageVenue.Bay), 0f, "isolated cage cannot spread");
        Near(DiseaseSpread.OutbreakMtb(CageVenue.OpenSea, 10f, 0.8f, 0.5f, 1f), 40f, "outbreak every 40 days");
        Near(DiseaseSpread.OutbreakMtb(CageVenue.Bay, 10f, 0.8f, 0.5f, 1f), 32f, "bay x0.8");
        Near(DiseaseSpread.OutbreakMtb(CageVenue.OpenSea, 20f, 0.4f, 1.0f, 1f), 40f * 0.343f, "warm, low O2 and crowded stack x0.7 each", 0.01f);
        Near(DiseaseSpread.AfterDay(0.3f), 0.33f, "+0.03 a day");
        Near(DiseaseSpread.AfterDay(0.99f), 1f, "capped at 1");
        Near(DiseaseSpread.AfterIsolatedDay(0.5f, 5), 0.53f, "isolated under 10 days still rises");
        Near(DiseaseSpread.AfterIsolatedDay(0.5f, 10), 0.48f, "isolated 10 days: heals 0.02 a day");
        Near(DiseaseSpread.AfterTreatment(0.9f, false), 0.5f, "treatment -0.4");
        Near(DiseaseSpread.AfterTreatment(0.9f, true), 0.7f, "herbal half as good");
        Near(DiseaseSpread.AfterTreatment(0.2f, false), 0f, "not below 0");

        // ── 魚影 ──
        Near(FishShadowField.PerCage(true, true, true), 0.6f, "a fed stocked cage in a bloom: 0.6");
        Near(FishShadowField.Clamp(2.4f), 1f, "density capped at 1");
        Check(FishShadowField.FlecksToSpawn(1f, 0) == 4 && FishShadowField.FlecksToSpawn(0.2f, 0) == 1, "ceil(density x 4) per burst");
        Check(FishShadowField.FlecksToSpawn(1f, 38) == 2 && FishShadowField.FlecksToSpawn(1f, 40) == 0, "never more than 40 alive");
        Near(MapComponent_Aquaculture.RectGap(new Verse.CellRect(0, 0, 7, 7), new Verse.CellRect(8, 0, 7, 7)), 1f, "rect gap: one cell between cages");
        Near(MapComponent_Aquaculture.RectGap(new Verse.CellRect(0, 0, 7, 7), new Verse.CellRect(7, 0, 7, 7)), 0f, "touching cages: gap 0");
    }

    static void ContentAquaculture()
    {
        XElement[] all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToArray();
        XElement Def(string name) => all.FirstOrDefault(d => (string)d.Element("defName") == name);
        float F(XElement e, string field) => float.Parse(e.Element(field).Value, CultureInfo.InvariantCulture);

        // ── 品種 ──
        XElement[] species = all.Where(d => d.Name.LocalName == "DepthRim.AquacultureSpeciesDef").ToArray();
        Check(species.Length == 7, $"seven farmable species ({species.Length})");
        string[] coastal = { "Cod", "Salmon", "Bluefish", "Flounder" };
        foreach (XElement s in species)
        {
            string name = (string)s.Element("defName");
            XElement variant = Def((string)s.Element("variant"));
            if (!Check(variant != null && variant.Name.LocalName == "DepthRim.FishVariantDef", $"{name}: variant exists")) { continue; }
            string[] t = variant.Element("temperature").Value.Split('~');
            float tmin = float.Parse(t[0], CultureInfo.InvariantCulture), tmax = float.Parse(t[1], CultureInfo.InvariantCulture);
            List<string> layers = s.Element("netLayers").Elements("li").Select(e => e.Value).ToList();
            Check(layers.Count > 0, $"{name}: has net layers");
            Check(F(s, "stockCount") * F(s, "harvestWeight") <= 300.01f, $"{name}: stockCount x harvestWeight <= 300 kg");
            string[] y = s.Element("yieldPerFish").Value.Split('~');
            Check(int.Parse(y[0]) >= 1 && int.Parse(y[1]) >= int.Parse(y[0]), $"{name}: yieldPerFish valid");
            bool isCoastal = (string)s.Element("coastal") == "true";
            Check(isCoastal == coastal.Any(c => name.EndsWith(c)), $"{name}: coastal flag matches the design");
            if (isCoastal)
            {
                Check(layers.Contains("Shallow"), $"{name}: coastal species can use the surface net");
                // 灣內水溫（年均 −10～28 °C 的地方）約 −1.8～29.5：適溫要和它有交集
                Check(tmax > -1.8f && tmin < 29.5f, $"{name}: temperature range overlaps the bay range");
            }
            // 網層與溫區有交集：用溫帶外海的網袋中點水溫粗估
            Dictionary<string, (float lo, float hi)> layerTemps = new Dictionary<string, (float, float)> { { "Shallow", (-1.8f, 29.5f) }, { "Midwater", (-1.1f, 22f) }, { "Deep", (0f, 10f) } };
            Check(layers.Any(l => layerTemps[l].hi > tmin && layerTemps[l].lo < tmax), $"{name}: some net layer suits its temperature");
        }
        Check(Def("DepthRim_Aquaculture_Frostfish").Element("netLayers").Elements("li").Select(e => e.Value).SequenceEqual(new[] { "Deep" }), "frostfish: deep nets only");
        Check(!Def("DepthRim_Aquaculture_Cod").Element("netLayers").Elements("li").Any(e => e.Value == "Deep") && !Def("DepthRim_Aquaculture_Tuna").Element("netLayers").Elements("li").Any(e => e.Value == "Deep"), "cod and tuna are not in deep nets");

        // ── 箱網 Def ──
        (string def, float depth, float min, int hp)[] cages = { ("DepthRim_NetCage_Shallow", 10, 10, 300), ("DepthRim_NetCage_Midwater", 150, 180, 360), ("DepthRim_NetCage_Deep", 500, 550, 420) };
        foreach (var c in cages)
        {
            XElement d = Def(c.def);
            if (!Check(d != null, $"{c.def} exists")) { continue; }
            XElement comp = d.Element("comps")?.Elements("li").FirstOrDefault(li => (string)li.Attribute("Class") == "DepthRim.CompProperties_NetCage");
            if (!Check(comp != null, $"{c.def} has CompProperties_NetCage")) { continue; }
            Check(F(comp, "netDepth") == c.depth && F(comp, "minWaterDepth") == c.min && F(comp, "minWaterDepth") >= F(comp, "netDepth"), $"{c.def}: net at {c.depth} m, needs {c.min} m");
            Check(F(comp, "stormDamageFactor") == 1.5f && F(comp, "capacityKg") == 300f && F(comp, "feedCapacity") == 240f, $"{c.def}: storm x1.5, 300 kg, 240 kibble");
            Check(F(d.Element("statBases"), "MaxHitPoints") == c.hp, $"{c.def}: {c.hp} HP");
        }
        XElement baseDef = all.FirstOrDefault(d => (string)d.Attribute("Name") == "DepthRim_NetCageBase");
        Check(baseDef?.Element("size")?.Value == "(7,7)" && baseDef.Element("rotatable")?.Value == "false", "net cages are 7x7 and not rotatable");
        Check(baseDef?.Element("building")?.Element("repairable")?.Value == "false", "vanilla repair cannot mend nets for free");
        Check(baseDef?.Element("terrainAffordanceNeeded")?.Value == "DepthRim_HullBase", "net cages need the hull-base affordance");
        XElement gfx = baseDef?.Element("graphicData");
        Check(gfx?.Element("shaderType")?.Value == "CutoutComplex" && File.Exists(Path.Combine(ModRoot, "Textures", gfx.Element("texPath").Value + ".png"))
              && File.Exists(Path.Combine(ModRoot, "Textures", gfx.Element("maskPath").Value + ".png")), "net cage texture and mask exist; the mask colours the floats (CutoutComplex)");

        // ── 配方 ──
        XElement recipe = Def("DepthRim_MakeFishKibble");
        if (Check(recipe != null, "fish kibble recipe exists"))
        {
            Check((string)recipe.Element("researchPrerequisite") == "DepthRim_Aquaculture", "unlocked by aquaculture");
            Check(recipe.Element("products").Element("Kibble").Value == "40", "makes 40 kibble");
            Check(recipe.Element("fixedIngredientFilter").Element("categories").Elements("li").Select(e => e.Value).SequenceEqual(new[] { "Fish" }), "fish only");
            Check(recipe.Element("recipeUsers").Elements("li").Select(e => e.Value).OrderBy(x => x).SequenceEqual(new[] { "ButcherSpot", "TableButcher" }), "at the butcher spot and table");
        }

        // ── 工作 ──
        string[] givers = { "DepthRim_StockCage", "DepthRim_HarvestCage", "DepthRim_RefillCageFeed", "DepthRim_RepairCageNet", "DepthRim_TreatCage" };
        foreach (string g in givers)
        {
            XElement w = all.FirstOrDefault(d => d.Name.LocalName == "WorkGiverDef" && (string)d.Element("defName") == g);
            Check(w != null && new[] { "Handling", "Construction", "Doctor" }.Contains((string)w.Element("workType")), $"{g}: on Handling/Construction/Doctor");
        }
        Check(!all.Any(d => d.Name.LocalName == "WorkTypeDef"), "no new work types");

        // ── 研究 ──
        Check((string)Def("DepthRim_Aquaculture").Element("tab") == "DepthRim_ResearchTab" && Def("DepthRim_Aquaculture").Element("prerequisites").Elements("li").Any(e => e.Value == "Fishing"), "aquaculture: underwater tab, needs Fishing");
        Check((string)Def("DepthRim_DeepNetCages").Element("knowledgeCategory") == "DepthRim_Seabed", "deep net cages need seabed research");

        // ── 水獺補丁、模組設定 ──
        string otter = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Patches", "Aquaculture_Otter.xml"));
        Check(otter.Contains("ThingDef[defName=\"Otter\"]") && otter.Contains("DepthRim.CompProperties_NetRaider"), "Odyssey otters get the (inactive) net-raider comp");
        Check(new DepthRimModSettings().enableNetCages, "net cages are on by default");
    }

    static void AquacultureHabitat()
    {
        // 浮動：幅度隨天氣，小於一格；不同相位不同步
        UnityEngine.Vector3 calm = Building_NetCage.FloatOffset(3.7f, 0f, 1f);
        float maxCalm = 0f, maxStorm = 0f;
        for (float t = 0f; t < 60f; t += 0.25f)
        {
            maxCalm = Mathf.Max(maxCalm, Building_NetCage.FloatOffset(t, 1f, 1f).magnitude);
            maxStorm = Mathf.Max(maxStorm, Building_NetCage.FloatOffset(t, 1f, 2.5f).magnitude);
        }
        Check(maxCalm > 0.02f && maxCalm < 0.2f, $"net cages bob a little in calm water ({maxCalm:F3} cells)");
        Near(maxStorm / maxCalm, 2.5f, "and more in a storm (x sway factor)", 0.01f);
        Check(calm.y == 0f && (Building_NetCage.FloatOffset(3.7f, 5f, 1f) - calm).magnitude > 0.001f, "cages with different phases bob out of step");
        string sea = File.ReadAllText(Path.Combine(ModRoot, ".source", "DepthRim", "SeaSurface", "SeaSurface.cs"));
        Check(sea.Contains("Building_NetCage net in aqua.Cages") && sea.Contains("OccupiedRect().EdgeCells"), "net cage frames go into the sea mask (edge foam)");
        // 浮框白沫在 g：貼著浮框、不超過柱影門檻
        int n = 21;
        bool[] ring = new bool[n * n];
        for (int z = 7; z <= 13; z++) { for (int x = 7; x <= 13; x++) { ring[z * n + x] = x == 7 || x == 13 || z == 7 || z == 13; } }
        var cageField = new PlatformShadowField(n, n, new bool[n * n], new bool[n * n], new bool[n * n], ring);
        float onFrame = cageField.pillar[10 * n + 7], inside = cageField.pillar[10 * n + 10], outside = cageField.pillar[10 * n + 4];
        Check(onFrame > 0.25f && onFrame <= PlatformShadowField.CageFoamMax + 0.0001f && onFrame < PlatformShadow.PillarShadowStart, $"strong foam on the frame, below the pillar-shadow start ({onFrame:F3})");
        Check(inside < 0.02f && outside < 0.02f, $"no foam in the middle of the cage or 3 cells out ({inside:F3}, {outside:F3})");
        Check(cageField.hull.All(v => v == 0f), "cages add no hull (no deck offset or hull shadow)");
        // 貼圖大小：浮框外緣貼齊佔地
        XElement cageBase = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).First(d => (string)d.Attribute("Name") == "DepthRim_NetCageBase");
        float drawSize = float.Parse(cageBase.Element("graphicData").Element("drawSize").Value.Trim('(', ')').Split(',')[0], CultureInfo.InvariantCulture);
        Near(drawSize * 908f / 1024f, 7f, "the drawn frame (908 of 1024 px) spans the 7-cell footprint", 0.01f);

        // 逃逸魚：沒有水位的地圖，海面地形也算水（實際判斷在遊戲內，這裡檢查程式有接上）
        string habitat = File.ReadAllText(Path.Combine(ModRoot, ".source", "DepthRim", "Fish", "FishHabitat.cs"));
        Check(habitat.Contains("OnSurfaceWater(tmpFish[i]) ? 1f : 0f"), "fish on surface water do not suffocate on maps without water levels");
        string weather = File.ReadAllText(Path.Combine(ModRoot, ".source", "DepthRim", "SeaSurface", "MapComponent_SeaWeather.cs"));
        Check(weather.Contains("stormDamageFactor") && weather.Contains("Notify_StormHit"), "sea weather scales net cage damage and rolls for a breach");
    }
}
