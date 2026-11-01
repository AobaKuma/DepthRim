using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 海洋圖層（取代冰洋）：生態區分配規則（OceanBiomeSelector）與七個生態區的內容一致性。
static partial class Test
{
    static OceanBiomeInput Surface(float temp, bool water = true, bool coastal = false, bool ice = false, float vent = 0.5f, float trench = 0.9f, float variation = 0.3f)
        => new OceanBiomeInput { surfaceKnown = true, surfaceTemperature = temp, surfaceWater = water, coastal = coastal, surfaceIce = ice, ventNoise = vent, trenchNoise = trench, variationNoise = variation };

    static void OceanBiomes()
    {
        Check(OceanBiomeSelector.Choose(Surface(-20f)) == OceanBiome.PolarIceCap, "very cold surface: polar ice cap");
        Check(OceanBiomeSelector.Choose(Surface(5f, ice: true)) == OceanBiome.PolarIceCap, "ice sheet / sea ice above: polar ice cap");
        Check(OceanBiomeSelector.Choose(Surface(-20f, vent: 0.99f)) == OceanBiome.PolarIceCap, "polar wins over a vent hotspot");
        Check(OceanBiomeSelector.Choose(Surface(26f, coastal: true)) == OceanBiome.ShallowReef, "warm coast: shallow coral reef");
        Check(OceanBiomeSelector.Choose(Surface(12f, coastal: true)) == OceanBiome.KelpForest, "cool coast: kelp forest");
        Check(OceanBiomeSelector.Choose(Surface(-5f, coastal: true)) == OceanBiome.PolarIceCap, "freezing coast: polar ice cap");
        Check(OceanBiomeSelector.Choose(Surface(15f, vent: 0.95f)) == OceanBiome.HydrothermalField, "open ocean hotspot: hydrothermal vent field");
        Check(OceanBiomeSelector.Choose(Surface(15f, coastal: true, vent: 0.95f)) == OceanBiome.KelpForest, "no vents right on the coast");
        Check(OceanBiomeSelector.Choose(Surface(15f, trench: 0.5f)) == OceanBiome.AbyssalTrench, "trench noise on its centre line: abyssal trench");
        Check(OceanBiomeSelector.Choose(Surface(15f, trench: 0.6f)) != OceanBiome.AbyssalTrench, "off the centre line: no trench");
        Check(OceanBiomeSelector.Choose(Surface(15f, variation: 0.8f)) == OceanBiome.DeepReef, "warm open ocean with high variation: deep coral reef");
        Check(OceanBiomeSelector.Choose(Surface(2f, variation: 0.8f)) == OceanBiome.SeabedPlain, "cold open ocean: seabed plain");
        Check(OceanBiomeSelector.Choose(Surface(15f, water: false)) == OceanBiome.SeabedPlain, "under a continent: seabed plain");
        Check(OceanBiomeSelector.Choose(new OceanBiomeInput { surfaceTemperature = 10f, trenchNoise = 0.9f }) == OceanBiome.SeabedPlain, "no surface data: falls back to the noise rules");
        Check(OceanBiomeSelector.DefName(OceanBiome.ShallowReef) == "DepthRim_ShallowReef", "biome kind maps to its defName");
    }

    static void ContentOcean()
    {
        string file = Path.Combine(ModRoot, "1.7", "Defs", "World", "Ocean_Biomes.xml");
        if (!Check(File.Exists(file), "Ocean_Biomes.xml exists")) { return; }
        XDocument doc = XDocument.Load(file);
        var biomes = doc.Root.Elements("BiomeDef").Where(b => (string)b.Attribute("Abstract") != "True").ToList();
        foreach (OceanBiome kind in Enum.GetValues(typeof(OceanBiome)))
        {
            string name = OceanBiomeSelector.DefName(kind);
            XElement def = biomes.FirstOrDefault(b => b.Element("defName")?.Value == name);
            if (!Check(def != null, $"{kind} has a BiomeDef {name}")) { continue; }
            XElement ext = def.Descendants("li").FirstOrDefault(li => (string)li.Attribute("Class") == "DepthRim.WaterMapExtension");
            Check(ext != null, $"{name} is a water map (WaterMapExtension)");
            Check(ext != null && Enum.TryParse(ext.Element("depthTier")?.Value, out DepthTier _), $"{name} declares its depth tier");
            Check(def.Element("forceRockTypes")?.Elements("li").Any() == true, $"{name} forces its rock types (coral never leaks to the surface)");
        }
        Check(biomes.Count == Enum.GetValues(typeof(OceanBiome)).Length, $"exactly one BiomeDef per ocean biome ({biomes.Count})");
        XElement oceanBase = doc.Root.Elements("BiomeDef").FirstOrDefault(b => (string)b.Attribute("Name") == "DepthRim_OceanBiomeBase");
        Check(oceanBase != null, "biomes share the abstract ocean base");
        XElement weather = oceanBase?.Element("baseWeatherCommonalities");
        Check(weather != null && (string)weather.Attribute("Inherit") == "False" && weather.Elements().Select(e => e.Name.LocalName).SequenceEqual(new[] { "DepthRim_Undersea" }),
            "ocean biomes only have the undersea weather (the readout says undersea, not underground)");

        // 升降井到得了的生態區（淺海、變溫層）至少一個，深的生態區存在（留給潛艇）
        var tiers = biomes.Select(b => Enum.Parse<DepthTier>(b.Descendants("depthTier").First().Value)).ToList();
        Check(tiers.Any(t => t <= DepthTier.Thermocline) && tiers.Any(t => t >= DepthTier.Deep), "both shaft-reachable and deep biomes exist");
        Check(new WaterMapExtension { depthTier = DepthTier.Thermocline }.ShaftAccessible && !new WaterMapExtension { depthTier = DepthTier.Deep }.ShaftAccessible,
            "the ice shaft reaches shallow and mid-depth water only");

        // 牆體貼圖（使用者提供）都有被 Def 用到
        foreach (string tex in new[] { "coralWall", "coralWallDark", "ShipHull" })
        {
            Check(File.Exists(Path.Combine(ModRoot, "Textures", "Thing", tex + ".png")), $"texture Thing/{tex}.png exists");
            Check(DefFiles.Any(f => File.ReadAllText(f).Contains("<texPath>Thing/" + tex + "</texPath>")), $"a Def uses Thing/{tex}");
        }
        Check(!File.Exists(Path.Combine(ModRoot, "1.7", "Defs", "World", "IceOcean_Layer.xml")), "the old ice ocean layer file is gone");

        // 世界地圖的展開圖示：沒有 texture 的 WorldObjectDef 必須給 expandingIconColor，否則原版讀 Material.color 會 NullReference（使用者回報）
        foreach (string f in DefFiles)
        {
            foreach (XElement wo in XDocument.Load(f).Root.Elements("WorldObjectDef"))
            {
                if (wo.Element("expandingIcon")?.Value.Trim().ToLowerInvariant() != "true") { continue; }
                Check(wo.Element("expandingIconColor") != null || wo.Element("texture") != null,
                    $"{wo.Element("defName")?.Value}: expanding icon has expandingIconColor or a texture");
            }
        }
    }
}
