using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;
using RimWorld;
using Verse;

// 漁獲（2026-11-16）：三種體型的通用魚，依生態區、水深層級與水溫抽品種，死後變成品種的魚獲。
static partial class Test
{
    static FishVariantDef Variant(string name, FishSize size, float min, float max, float commonality = 1f, DepthTier[] tiers = null, BiomeDef[] biomes = null)
    {
        return new FishVariantDef
        {
            defName = name,
            size = size,
            temperature = new FloatRange(min, max),
            commonality = commonality,
            tiers = tiers?.ToList() ?? new List<DepthTier>(),
            biomes = biomes?.ToList() ?? new List<BiomeDef>()
        };
    }

    static void FishVariantSelection()
    {
        BiomeDef kelp = new BiomeDef { defName = "Kelp" };
        BiomeDef plain = new BiomeDef { defName = "Plain" };
        FishVariantDef cod = Variant("cod", FishSize.Small, -2f, 12f, tiers: new[] { DepthTier.Shallow, DepthTier.Thermocline });
        FishVariantDef bluefish = Variant("bluefish", FishSize.Small, 10f, 32f, tiers: new[] { DepthTier.Shallow, DepthTier.Thermocline });
        FishVariantDef salmon = Variant("salmon", FishSize.Small, 0f, 16f, 3f, biomes: new[] { kelp });
        FishVariantDef grenadier = Variant("grenadier", FishSize.Small, -2f, 14f, tiers: new[] { DepthTier.Deep, DepthTier.Seabed });
        FishVariantDef tuna = Variant("tuna", FishSize.Large, 14f, 32f, tiers: new[] { DepthTier.Shallow });
        List<FishVariantDef> all = new List<FishVariantDef> { cod, bluefish, salmon, grenadier, tuna };

        Check(FishVariantSelector.Choose(all, FishSize.Small, plain, DepthTier.Shallow, 2f, 0.5f) == cod, "cold shallow water: cod (bluefish too warm, salmon not in this biome)");
        Check(FishVariantSelector.Choose(all, FishSize.Small, plain, DepthTier.Shallow, 25f, 0.5f) == bluefish, "warm shallow water: bluefish");
        Check(FishVariantSelector.Choose(all, FishSize.Small, plain, DepthTier.Deep, 3f, 0.99f) == grenadier, "deep water: only the deep variant");
        Check(FishVariantSelector.Choose(all, FishSize.Large, plain, DepthTier.Shallow, 25f, 0f) == tuna, "size is respected: large picks tuna");
        Check(FishVariantSelector.Choose(all, FishSize.Medium, plain, DepthTier.Shallow, 25f, 0f) == null, "no variant of that size: null");
        // 權重：kelp 5 °C → cod (1) 與 salmon (3) 都合；roll 0.2 落在 cod，0.5 落在 salmon
        Check(FishVariantSelector.Choose(all, FishSize.Small, kelp, DepthTier.Shallow, 5f, 0.2f) == cod, "weighted pick: low roll gets cod (weight 1 of 4)");
        Check(FishVariantSelector.Choose(all, FishSize.Small, kelp, DepthTier.Shallow, 5f, 0.5f) == salmon, "weighted pick: higher roll gets salmon (weight 3 of 4)");
        // 水溫都不合：取棲地合、最接近的
        Check(FishVariantSelector.Choose(all, FishSize.Large, plain, DepthTier.Shallow, 0f, 0.5f) == tuna, "too cold for every variant: the closest one in this habitat");
        Check(FishVariantSelector.Choose(all, FishSize.Small, plain, DepthTier.Deep, 30f, 0.5f) == grenadier, "too warm in the deep: still the deep variant, not a shallow one");
        // 棲地都不合（例如 null 生態區、陸地）：同體型裡水溫最接近的
        Check(FishVariantSelector.Choose(all, FishSize.Large, null, null, 20f, 0.5f) == tuna, "no habitat match: fall back to any variant of that size");
        Check(FishVariantSelector.Choose(all, FishSize.Small, null, null, 30f, 0.5f) == bluefish, "no habitat match: the variant closest in temperature");
    }

    static void ContentFish()
    {
        string races = Path.Combine(ModRoot, "1.7", "Defs", "Fish", "Fish_Races.xml");
        string variantsPath = Path.Combine(ModRoot, "1.7", "Defs", "Fish", "Fish_Variants.xml");
        XDocument racesDoc = XDocument.Load(races);
        // 三種體型：貼圖存在（Graphic_Multi 需要 _north/_east/_south）
        foreach (XElement kind in racesDoc.Root.Elements("PawnKindDef"))
        {
            string tex = kind.Descendants("texPath").First().Value;
            foreach (string dir in new[] { "north", "east", "south" })
            {
                Check(File.Exists(Path.Combine(ModRoot, "Textures", tex.Replace('/', Path.DirectorySeparatorChar) + "_" + dir + ".png")), $"{kind.Element("defName").Value} texture {tex}_{dir} exists");
            }
        }
        Dictionary<FishSize, string> raceBySize = racesDoc.Root.Elements("ThingDef")
            .Where(t => t.Element("defName") != null)
            .ToDictionary(t => (FishSize)Enum.Parse(typeof(FishSize), t.Descendants("size").First().Value), t => t.Element("defName").Value);
        Check(raceBySize.Count == 3, "one fish race per size");

        // 品種：魚獲是 Odyssey 的魚
        XDocument odysseyFish = XDocument.Load(Path.Combine(ModRoot, "..", "..", "Data", "Odyssey", "Defs", "ThingDefs_Items", "Items_Resource_Fish.xml"));
        HashSet<string> fishItems = new HashSet<string>(odysseyFish.Root.Elements("ThingDef").Select(t => (string)t.Element("defName")).Where(n => n != null));
        // 每種魚獲的營養值（沒寫的沿用 FishBase）
        float baseNutrition = float.Parse(odysseyFish.Root.Elements("ThingDef").Single(t => (string)t.Attribute("Name") == "FishBase").Element("statBases").Element("Nutrition").Value);
        Dictionary<string, float> nutrition = odysseyFish.Root.Elements("ThingDef").Where(t => t.Element("defName") != null)
            .ToDictionary(t => t.Element("defName").Value, t => float.Parse(t.Element("statBases")?.Element("Nutrition")?.Value ?? baseNutrition.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture));
        XDocument variantsDoc = XDocument.Load(variantsPath);
        Dictionary<string, BiomeDef> biomeStubs = new Dictionary<string, BiomeDef>();
        BiomeDef Stub(string name) => biomeStubs.TryGetValue(name, out BiomeDef b) ? b : biomeStubs[name] = new BiomeDef { defName = name };
        List<FishVariantDef> core = new List<FishVariantDef>();
        foreach (XElement v in variantsDoc.Root.Elements("DepthRim.FishVariantDef"))
        {
            string name = v.Element("defName").Value;
            Check(fishItems.Contains(v.Element("catchDef").Value), $"{name} catch {v.Element("catchDef").Value} is an Odyssey fish");
            // 體型決定魚獲的尺寸（使用者要求）：小 → 極小／一般、中 → 一般、大 → 大型
            FishSize variantSize = (FishSize)Enum.Parse(typeof(FishSize), v.Element("size").Value);
            if (nutrition.TryGetValue(v.Element("catchDef").Value, out float n))
            {
                FishCatchClass cls = FishCatchSizes.ClassOf(n);
                Check(FishCatchSizes.Allows(variantSize, cls), $"{name}: a {variantSize} fish drops a {cls} catch ({v.Element("catchDef").Value}, nutrition {n})");
            }
            string[] t = v.Element("temperature").Value.Split('~');
            FishVariantDef def = Variant(name, (FishSize)Enum.Parse(typeof(FishSize), v.Element("size").Value), float.Parse(t[0]), float.Parse(t[1]),
                tiers: v.Element("tiers")?.Elements("li").Select(e => (DepthTier)Enum.Parse(typeof(DepthTier), e.Value)).ToArray(),
                biomes: v.Element("biomes")?.Elements("li").Select(e => Stub(e.Value)).ToArray());
            // 需要 DLC 的品種（MayRequire）不算進覆蓋率：沒有那個 DLC 時也要有魚
            if (v.Attribute("MayRequire") == null) { core.Add(def); }
        }
        Check(core.Count >= 9, $"enough fish variants ({core.Count})");

        // 每個有魚的海洋生態區、每種體型，在它實際的水溫範圍內都有水溫合適的品種（不必退而求其次）
        XDocument biomesDoc = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "World", "Ocean_Biomes.xml"));
        int biomesWithFish = 0;
        foreach (XElement b in biomesDoc.Root.Elements("BiomeDef").Where(e => e.Element("wildAnimals") != null))
        {
            biomesWithFish++;
            string name = b.Element("defName").Value;
            DepthTier tier = (DepthTier)Enum.Parse(typeof(DepthTier), b.Descendants("depthTier").First().Value);
            Check(raceBySize.Values.All(r => b.Element("wildAnimals").Element(r) != null), $"{name} spawns all three fish sizes");
            bool subglacial = OceanBiomeSelector.IsSubglacial(name);
            bool shallowish = tier <= DepthTier.Thermocline;
            float min = subglacial ? OceanTemperature.FreezingPoint : shallowish ? OceanTemperature.FreezingPoint : 0.5f;
            float max = subglacial ? OceanTemperature.SubglacialDeepWater
                : name == "DepthRim_HydrothermalField" ? 3f + OceanTemperature.VentWarming + 3.5f
                : shallowish ? OceanTemperature.MaxSurfaceWater : 5f;
            foreach (FishSize size in Enum.GetValues(typeof(FishSize)))
            {
                List<float> gaps = new List<float>();
                for (float temp = min; temp <= max + 0.001f; temp += 0.5f)
                {
                    if (!core.Any(v => v.size == size && v.HabitatMatches(Stub(name), tier) && v.TemperatureDistance(temp) == 0f)) { gaps.Add(temp); }
                }
                Check(gaps.Count == 0, $"{name} ({tier}, {min:0.#}~{max:0.#} °C) has a {size} fish variant at every temperature" + (gaps.Count > 0 ? $" (none at {string.Join(", ", gaps.Select(g => g.ToString("0.#")))})" : ""));
            }
        }
        Check(biomesWithFish >= 10, $"ocean biomes have fish ({biomesWithFish})");
        Check(biomesDoc.Root.Elements("BiomeDef").Where(e => e.Element("defName")?.Value == "DepthRim_Landmass").All(e => e.Element("wildAnimals") == null), "no fish under the landmass");

        // 繁中翻譯：每個品種都有名稱
        string zh = Directory.GetDirectories(Path.Combine(ModRoot, "1.7", "Languages")).First(d => Path.GetFileName(d).StartsWith("ChineseTraditional"));
        XDocument zhVariants = XDocument.Load(Path.Combine(zh, "DefInjected", "DepthRim.FishVariantDef", "DepthRim_FishVariants.xml"));
        foreach (XElement v in variantsDoc.Root.Elements("DepthRim.FishVariantDef"))
        {
            string name = v.Element("defName").Value;
            Check(zhVariants.Root.Element(name + ".label") != null, $"{name} has a Chinese label");
        }
    }
}

static partial class Test
{
    static void FishHabitatRules()
    {
        Check(FishCatchSizes.ClassOf(0.05f) == FishCatchClass.Tiny && FishCatchSizes.ClassOf(0.25f) == FishCatchClass.Normal && FishCatchSizes.ClassOf(0.5f) == FishCatchClass.Large,
            "Odyssey fish sizes: guppy/toxfish tiny, cod normal, tuna large");
        Check(FishCatchSizes.Allows(FishSize.Small, FishCatchClass.Tiny) && FishCatchSizes.Allows(FishSize.Small, FishCatchClass.Normal) && !FishCatchSizes.Allows(FishSize.Small, FishCatchClass.Large),
            "small fish drop tiny or normal catches");
        Check(FishCatchSizes.Allows(FishSize.Medium, FishCatchClass.Normal) && !FishCatchSizes.Allows(FishSize.Medium, FishCatchClass.Tiny) && !FishCatchSizes.Allows(FishSize.Medium, FishCatchClass.Large),
            "medium fish drop normal catches");
        Check(FishCatchSizes.Allows(FishSize.Large, FishCatchClass.Large) && !FishCatchSizes.Allows(FishSize.Large, FishCatchClass.Normal),
            "large fish drop large catches");
        Check(FishHabitat.OutOfWater(0f) && FishHabitat.OutOfWater(0.29f), "a drained or ankle-deep cell is out of water for fish");
        Check(!FishHabitat.OutOfWater(FishHabitat.MinWaterLevel) && !FishHabitat.OutOfWater(1f), "enough water: fish breathe");
        Check(FishHabitat.UpdatesToDeath >= 2 && FishHabitat.UpdatesToDeath <= 4, $"stranded fish die quickly ({FishHabitat.UpdatesToDeath} updates of {MapComponent_Flooding.UpdateInterval} ticks)");

        XDocument hediffs = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "Fish", "Fish_Hediffs.xml"));
        XElement outOfWater = hediffs.Root.Elements("HediffDef").Single(e => e.Element("defName").Value == "DepthRim_FishOutOfWater");
        Check(outOfWater.Element("lethalSeverity")?.Value == "1", "out of water is lethal");
        Check(outOfWater.Descendants("capMods").Elements("li").Any(li => li.Element("capacity")?.Value == "Moving" && li.Element("setMax")?.Value == "0")
            && outOfWater.Element("stages").Elements("li").First().Element("minSeverity")?.Value == "0", "out of water downs the fish from the first moment (Moving 0)");

        // 不參與原版不看生態區的生成
        XDocument races = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "Fish", "Fish_Races.xml"));
        XElement race = races.Root.Elements("ThingDef").Single(e => (string)e.Attribute("Name") == "DepthRim_FishBase").Element("race");
        Check(race.Element("herdMigrationAllowed")?.Value == "false" && race.Element("canBecomeShambler")?.Value == "false" && race.Element("neverIncludeInQuests")?.Value == "true",
            "fish race: no herd migration, shamblers or quests");
        Check(races.Root.Elements("PawnKindDef").All(k => k.Element("canArriveManhunter")?.Value == "false"), "fish kinds never arrive as manhunter packs");
        // 游離地圖（比照 Odyssey 的鳥）：只用離場／進場飛行，平常不在地圖上飛
        Check(race.Element("canLeaveMapFlying")?.Value == "true" && race.Element("canFlyIntoMap")?.Value == "true" && race.Element("flightStartChanceOnJobStart")?.Value == "0",
            "fish swim off and in like Odyssey birds, but never fly around the map");
        XElement stats = races.Root.Elements("ThingDef").Single(e => (string)e.Attribute("Name") == "DepthRim_FishBase").Element("statBases");
        Check(float.Parse(stats.Element("MaxFlightTime")?.Value ?? "0") > 0f, "MaxFlightTime > 0 (vanilla CanEverFly needs it to leave flying)");
        // 只有水下海洋生態區把魚列為野生動物（全模組的 BiomeDef）
        foreach (string file in DefFiles)
        {
            foreach (XElement biome in XDocument.Load(file).Root.Elements("BiomeDef").Where(b => b.Element("wildAnimals")?.Elements().Any(a => a.Name.LocalName.StartsWith("DepthRim_Fish")) == true))
            {
                Check(biome.Element("modExtensions")?.Elements("li").Any(li => (string)li.Attribute("Class") == "DepthRim.WaterMapExtension") == true,
                    $"{biome.Element("defName").Value} lists fish and is an underwater ocean biome");
            }
        }
    }
}
