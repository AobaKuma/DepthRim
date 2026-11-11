using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 海洋圖層（取代冰洋）：生態區分配規則（OceanBiomeSelector）與七個生態區的內容一致性。
static partial class Test
{
    const int NoRidge = OceanBiomeSelector.NoRidge;

    static OceanBiomeInput Surface(float temp, bool water = true, int coast = 20, bool ice = false, int ridge = NoRidge, bool active = false, float vent = 0.3f, float seamount = 0.3f)
        => new OceanBiomeInput { surfaceKnown = true, surfaceTemperature = temp, surfaceWater = water, coastDistance = water || ice ? coast : 0, surfaceIce = ice,
            ridgeDistance = ridge, activeMargin = active, ventNoise = vent, seamountNoise = seamount };

    static float Depth(OceanBiomeInput input, float noise = 0.5f) => OceanBiomeSelector.SeafloorDepth(input, noise);

    static void OceanBiomes()
    {
        // 陸地與冰
        Check(OceanBiomeSelector.Choose(Surface(15f, water: false)) == OceanBiome.Landmass, "dry land above: landmass, no ocean");
        Check(OceanBiomeSelector.Choose(Surface(-30f, water: false)) == OceanBiome.Landmass, "frozen tundra is still land: landmass");
        Check(OceanBiomeSelector.Choose(Surface(-30f, water: false, ice: true, coast: 0)) == OceanBiome.Landmass, "ice sheet above counts as land: landmass, no ocean (2026-11-11)");
        Check(OceanBiomeSelector.Choose(Surface(5f, ice: true, coast: 1)) == OceanBiome.PolarIceCap, "sea ice over the shelf: subglacial ocean (the ice shaft's ocean)");
        Check(OceanBiomeSelector.Choose(Surface(-20f, coast: 2)) == OceanBiome.KelpForest, "frozen shelf without sea ice: no subglacial ocean (kelp forest)");
        Check(Enumerable.Range(1, 12).All(c => OceanBiomeSelector.Choose(Surface(-30f, coast: c)) != OceanBiome.PolarIceCap),
              "the subglacial ocean only forms under sea ice, however cold the open water is");
        // 冰海下必定是地下冰洋（2026-11-11）：不分離岸遠近、不管下面原本是中洋脊、海溝或海山
        Check(new[] { Surface(-20f, ice: true), Surface(-20f, ice: true, ridge: 0, vent: 0.9f), Surface(-20f, ice: true, coast: 5, active: true), Surface(-20f, ice: true, ridge: 10, seamount: 0.97f), Surface(5f, ice: true, coast: 60) }
              .All(i => OceanBiomeSelector.Choose(i) == OceanBiome.PolarIceCap), "sea ice above always means subglacial ocean below");
        // 地下冰洋分深淺層（2026-11-11）：深度照一般海底地形，再依深度分層
        Check(OceanBiomeSelector.SubglacialFor(5f) == OceanBiome.PolarIceCap && OceanBiomeSelector.SubglacialFor(199f) == OceanBiome.PolarIceCap, "subglacial shallows: under 200 m");
        Check(OceanBiomeSelector.SubglacialFor(200f) == OceanBiome.SubglacialThermocline && OceanBiomeSelector.SubglacialFor(2099f) == OceanBiome.SubglacialThermocline, "subglacial thermocline: 200–2100 m");
        Check(OceanBiomeSelector.SubglacialFor(2100f) == OceanBiome.SubglacialDeep && OceanBiomeSelector.SubglacialFor(5999f) == OceanBiome.SubglacialDeep && OceanBiomeSelector.SubglacialFor(Depth(Surface(-20f, ice: true), 1f)) == OceanBiome.SubglacialDeep, "subglacial deep sea: 2100–6000 m, including ridgeless basins");
        Check(OceanBiomeSelector.SubglacialFor(6000f) == OceanBiome.SubglacialSeabed && OceanBiomeSelector.SubglacialFor(Depth(Surface(-20f, ice: true, coast: 4, active: true), 0f)) == OceanBiome.SubglacialSeabed, "subglacial abyssal seabed: 6000 m and deeper (trenches under the ice)");
        Check(Depth(Surface(-20f, ice: true)) > 4000f && Depth(Surface(-20f, ice: true, coast: 5, active: true)) > 6000f, "under sea ice the sea floor keeps its real shape: deep basins and trenches");
        Check(new[] { OceanBiome.PolarIceCap, OceanBiome.SubglacialThermocline, OceanBiome.SubglacialDeep, OceanBiome.SubglacialSeabed }.All(OceanBiomeSelector.IsSubglacial)
              && !OceanBiomeSelector.IsSubglacial(OceanBiome.SeabedPlain) && OceanBiomeSelector.IsSubglacial("DepthRim_SubglacialDeep"), "all four layers count as subglacial ocean");
        { XElement[] layers = new[] { "DepthRim_PolarIceCap", "DepthRim_SubglacialThermocline", "DepthRim_SubglacialDeep", "DepthRim_SubglacialSeabed" }
              .Select(d => XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "World", "Ocean_Biomes.xml")).Root.Elements("BiomeDef").Single(e => (string)e.Element("defName") == d).Element("modExtensions").Elements("li").Single(e => (string)e.Attribute("Class") == "DepthRim.WaterMapExtension")).ToArray();
          Check(layers.Select(e => (string)e.Element("depthTier")).SequenceEqual(new[] { "Shallow", "Thermocline", "Deep", "Seabed" }), "each subglacial layer has its own depth tier");
          // 冰下非常低光（2026-11-11）：四層都用冰下天氣，白天最亮 maxGlow 也遠低於原版的「黑暗」門檻 0.3
          XDocument biomeDoc = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "World", "Ocean_Biomes.xml"));
          Check(new[] { "DepthRim_PolarIceCap", "DepthRim_SubglacialThermocline", "DepthRim_SubglacialDeep", "DepthRim_SubglacialSeabed" }
                .All(d => biomeDoc.Root.Elements("BiomeDef").Single(e => (string)e.Element("defName") == d).Element("baseWeatherCommonalities")?.Elements().Select(e => e.Name.LocalName).SequenceEqual(new[] { "DepthRim_UnderIce" }) == true),
                "every subglacial layer only has the under-ice weather");
          XElement underIce = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "World", "Ocean_Weather.xml")).Root.Elements("WeatherDef").Single(e => (string)e.Element("defName") == "DepthRim_UnderIce");
          Check(float.Parse(underIce.Element("maxGlow").Value) <= 0.1f, "under the ice, daylight is capped very low (maxGlow 0.1 or less)");
          Check(Directory.GetFiles(Path.Combine(ModRoot, "1.7", "Languages", "ChineseTraditional (繁體中文)", "DefInjected", "WeatherDef")).Any(f => File.ReadAllText(f).Contains("<DepthRim_UnderIce.label>")), "the under-ice weather is translated");
          Check(layers.Select(e => (string)e.Element("seafloorDepth")).SequenceEqual(new[] { "3~200", "200~2000", "2100~6000", "6000~11000" }), "each layer's depth range matches the layer thresholds"); }
        Check(OceanBiomeSelector.Choose(Surface(5f, coast: 7)) == OceanBiome.SeabedPlain, "open water off an ice sheet coast is not subglacial ocean");
        // 被動大陸邊緣：寬棚、緩坡
        Check(OceanBiomeSelector.Choose(Surface(26f, coast: 1)) == OceanBiome.ShallowReef, "warm shelf: shallow coral reef");
        Check(OceanBiomeSelector.Choose(Surface(26f, coast: 2)) == OceanBiome.ShallowReef, "a passive shelf is two tiles wide");
        Check(OceanBiomeSelector.Choose(Surface(12f, coast: 1)) == OceanBiome.KelpForest, "cool shelf: kelp forest");
        Check(OceanBiomeSelector.Choose(Surface(-5f, coast: 1)) == OceanBiome.KelpForest, "freezing shelf without sea ice: kelp forest");
        Check(OceanBiomeSelector.Choose(Surface(15f, coast: 3)) == OceanBiome.DeepReef && OceanBiomeSelector.Choose(Surface(15f, coast: 4)) == OceanBiome.DeepReef, "continental slope: deep coral reef");
        Check(OceanBiomeSelector.Choose(Surface(15f, coast: 5)) == OceanBiome.SeabedPlain, "beyond a passive slope: no trench, abyssal plain");
        // 活動大陸邊緣（隱沒帶）：窄棚、陡坡、坡腳外平行海岸的海溝
        Check(OceanBiomeSelector.Choose(Surface(26f, coast: 2, active: true)) == OceanBiome.DeepReef, "an active margin has a one-tile shelf");
        Check(OceanBiomeSelector.Choose(Surface(15f, coast: 4, active: true)) == OceanBiome.AbyssalTrench && OceanBiomeSelector.Choose(Surface(15f, coast: 5, active: true)) == OceanBiome.AbyssalTrench,
            "an active margin has a trench four to five tiles offshore");
        Check(OceanBiomeSelector.Choose(Surface(15f, coast: 6, active: true)) == OceanBiome.SeabedPlain, "past the trench: abyssal plain");
        Check(OceanBiomeSelector.Choose(Surface(15f, coast: 30)) != OceanBiome.AbyssalTrench, "no trenches in the open ocean");
        // 中洋脊
        Check(OceanBiomeSelector.Choose(Surface(15f, ridge: 0)) == OceanBiome.MidOceanRidge, "ridge axis: mid-ocean ridge");
        Check(OceanBiomeSelector.Choose(Surface(15f, ridge: 0, vent: 0.8f)) == OceanBiome.HydrothermalField, "hot ridge segment: hydrothermal vent field");
        Check(OceanBiomeSelector.Choose(Surface(15f, ridge: 1)) == OceanBiome.MidOceanRidge, "ridge flank: mid-ocean ridge");
        Check(OceanBiomeSelector.Choose(Surface(15f, ridge: 4, vent: 0.99f)) == OceanBiome.SeabedPlain, "vents only on the ridge axis");
        Check(OceanBiomeSelector.Choose(Surface(15f, coast: 1, ridge: 0)) == OceanBiome.KelpForest, "the shelf wins over a ridge");
        // 海山
        Check(OceanBiomeSelector.Choose(Surface(15f, ridge: 10, seamount: 0.97f)) == OceanBiome.DeepReef, "seamount on the abyssal plain: deep coral reef");
        Check(OceanBiomeSelector.Choose(Surface(15f, ridge: 1, seamount: 0.97f)) == OceanBiome.MidOceanRidge, "no seamounts right on the ridge");
        Check(OceanBiomeSelector.Choose(new OceanBiomeInput { surfaceTemperature = 10f, ridgeDistance = NoRidge }) == OceanBiome.SeabedPlain, "no surface data: open ocean rules");
        // 靠近陸地邊緣的深度趨近地表視圖，往外漸變成海底地形深度（2026-11-11）
        Check(OceanBiomeSelector.BlendWithSurface(60f, 300f, 1) == 300f, "right next to land the ocean depth equals the surface view's depth");
        Check(OceanBiomeSelector.BlendWithSurface(4000f, 300f, OceanBiomeSelector.SurfaceBlendTiles) == 4000f && OceanBiomeSelector.BlendWithSurface(4000f, 300f, 30) == 4000f, "far from land the seafloor depth is unchanged");
        { float[] w = Enumerable.Range(1, OceanBiomeSelector.SurfaceBlendTiles).Select(OceanBiomeSelector.SurfaceBlendWeight).ToArray();
          Check(w.Zip(w.Skip(1), (a, b) => b < a).All(x => x) && w[0] == 1f && w[w.Length - 1] == 0f, "the surface weight fades smoothly from the coast outwards"); }
        Check(OceanBiomeSelector.BlendWithSurface(1000f, 200f, 3) > 200f && OceanBiomeSelector.BlendWithSurface(1000f, 200f, 3) < 1000f, "in between, the depth is a mix of both");
        Check(OceanBiomeSelector.BlendWithSurface(500f, -20f, 1) == 0f && OceanBiomeSelector.SurfaceBlendWeight(0) == 0f, "a surface tile above sea level counts as 0 m; land itself is not blended");
        // 中洋脊寬度沿著脊變化（2026-11-11：寬度太平均）
        Check(OceanBiomeSelector.RidgeFlank(0f) == OceanBiomeSelector.RidgeFlankTiles, "average ridge segment keeps the 1-tile flank");
        Check(OceanBiomeSelector.RidgeFlank(-0.3f) == 0 && OceanBiomeSelector.RidgeFlank(0.3f) == OceanBiomeSelector.MaxRidgeFlankTiles, "narrow segments are axis only, wide ones 3 tiles each side");
        Check(Enumerable.Range(-50, 101).Select(i => OceanBiomeSelector.RidgeFlank(i / 100f)).Distinct().Count() == 4, "ridge flank takes every width from 0 to 3");
        { OceanBiomeInput wide = Surface(15f, ridge: 3); wide.ridgeWidth = 0.3f; OceanBiomeInput narrow = Surface(15f, ridge: 1); narrow.ridgeWidth = -0.3f;
          Check(OceanBiomeSelector.Choose(wide) == OceanBiome.MidOceanRidge && OceanBiomeSelector.Choose(narrow) == OceanBiome.SeabedPlain, "wide segment: 3 tiles off-axis is still ridge; narrow segment: 1 tile off is plain"); }
        Check(OceanBiomeSelector.BasinDepth(6, 0.3f) < OceanBiomeSelector.BasinDepth(6) && OceanBiomeSelector.BasinDepth(6) < OceanBiomeSelector.BasinDepth(6, -0.3f), "wide ridge segments rise more gently (shallower at the same distance)");
        Check(OceanBiomeSelector.BasinDepth(0, 0.4f) == OceanBiomeSelector.BasinDepth(0, -0.4f) && OceanBiomeSelector.BasinDepth(NoRidge, 0.4f) == 5600f, "ridge crest and ridgeless basins don't depend on width");
        Check(OceanBiomeSelector.DefName(OceanBiome.MidOceanRidge) == "DepthRim_MidOceanRidge" && OceanBiomeSelector.DefName(OceanBiome.Landmass) == "DepthRim_Landmass", "biome kind maps to its defName");

        // 深度：棚淺、坡陡降、洋盆深；中洋脊比洋盆淺，離脊越遠越深；海溝最深
        float[] passive = Enumerable.Range(1, 9).Select(d => Depth(Surface(15f, coast: d))).ToArray();
        Check(passive.Zip(passive.Skip(1), (a, b) => b >= a).All(x => x), "passive margin: deeper with every tile from the coast");
        Check(Depth(Surface(15f, coast: 1), 1f) <= 200f && Depth(Surface(15f, coast: 2), 1f) <= 200f, "the passive shelf stays within 200 m");
        Check(Depth(Surface(15f, coast: 2, active: true), 0f) >= 200f, "the active shelf drops off after one tile");
        Check(Depth(Surface(15f, coast: 5, active: true), 0f) >= 7000f && Depth(Surface(15f, coast: 5, active: true), 1f) >= Depth(Surface(15f, coast: 30), 1f) + 2000f,
            "the trench is far deeper than the abyssal plain");
        Check(Depth(Surface(15f, coast: 7, active: true)) < Depth(Surface(15f, coast: 5, active: true)), "the sea floor rises again past the trench");
        float[] fromRidge = new[] { 0, 1, 3, 6, 12, 25 }.Select(r => Depth(Surface(15f, ridge: r))).ToArray();
        Check(fromRidge.Zip(fromRidge.Skip(1), (a, b) => b > a).All(x => x), "deeper with every step away from the ridge (cooling crust)");
        Check(Depth(Surface(15f, ridge: 0), 0f) >= 2100f && Depth(Surface(15f, ridge: 0), 1f) <= 3000f, "the ridge axis is about 2–3 km deep");
        Check(Depth(Surface(15f)) > 5000f, "a basin with no ridge is a deep abyssal plain");
        Check(Depth(Surface(15f, ridge: 10, seamount: 0.97f), 1f) <= 1500f, "seamount tops rise to 1.5 km or shallower");
        Check(OceanBiomeSelector.SubglacialFor(Depth(Surface(-20f, ice: true, coast: 1))) == OceanBiome.PolarIceCap, "the subglacial ocean on the shelf under sea ice is the shallow layer");
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
            if (kind == OceanBiome.Landmass)
            {
                // 陸塊：沒有海洋、不能進入
                Check(!def.Descendants("li").Any(li => (string)li.Attribute("Class") == "DepthRim.WaterMapExtension"), "the landmass is not a water map");
                Check(def.Element("impassable")?.Value == "true" && def.Element("canBuildBase")?.Value == "false", "the landmass cannot be entered or settled");
                Check(def.Element("workerClass")?.Value == "DepthRim.BiomeWorker_Ocean", "the landmass never appears on the surface");
                continue;
            }
            XElement ext = def.Descendants("li").FirstOrDefault(li => (string)li.Attribute("Class") == "DepthRim.WaterMapExtension");
            Check(ext != null, $"{name} is a water map (WaterMapExtension)");
            Check(ext != null && Enum.TryParse(ext.Element("depthTier")?.Value, out DepthTier _), $"{name} declares its depth tier");
            Check(def.Element("forceRockTypes")?.Elements("li").Any() == true, $"{name} forces its rock types (coral never leaks to the surface)");
            // 世界地圖：每個生態區都有自己的貼圖與海床深度
            string tex = def.Element("texture")?.Value;
            Check(tex != null && File.Exists(Path.Combine(ModRoot, "Textures", tex.Replace('/', Path.DirectorySeparatorChar) + ".png")),
                $"{name} has a world-map texture that exists ({tex})");
            Check(ext?.Element("seafloorDepth") != null, $"{name} declares its seafloor depth range");
        }

        // 海床深度：越深的層級越深，海溝最深；格子高度是負的
        var depthByTier = biomes.Where(b => b.Descendants("depthTier").Any()).Select(b => (tier: Enum.Parse<DepthTier>(b.Descendants("depthTier").First().Value),
            range: b.Descendants("seafloorDepth").FirstOrDefault()?.Value)).Where(x => x.range != null)
            .Select(x => (x.tier, min: float.Parse(x.range.Split('~')[0]), max: float.Parse(x.range.Split('~')[1]))).ToList();
        Check(depthByTier.All(x => x.min > 0f && x.max >= x.min), "seafloor depth ranges are positive metres below sea level");
        Check(depthByTier.Where(x => x.tier <= DepthTier.Thermocline).Max(x => x.max) < depthByTier.Where(x => x.tier >= DepthTier.Deep).Min(x => x.min),
            "shaft-reachable biomes are shallower than the deep and seabed biomes");
        var probe = new WaterMapExtension { seafloorDepth = new Verse.FloatRange(3000f, 5000f) };
        Check(probe.ElevationAt(0f) == -3000f && probe.ElevationAt(1f) == -5000f && probe.ElevationAt(2f) == -5000f,
            "tile elevation is the negative seafloor depth, clamped to the range");

        // 水深著色：越深越暗，淺水最亮最飽和
        float Lum(float d) { var c = Bathymetry.ColorAt(d); return 0.3f * c.r + 0.59f * c.g + 0.11f * c.b; }
        float Sat(float d) { var c = Bathymetry.ColorAt(d); int mx = Math.Max(c.r, Math.Max(c.g, c.b)), mn = Math.Min(c.r, Math.Min(c.g, c.b)); return mx == 0 ? 0 : (mx - mn) / (float)mx; }
        float[] probes = { 0f, 20f, 100f, 500f, 2000f, 4000f, 6000f, 9000f, 11000f };
        Check(probes.Zip(probes.Skip(1), (a, b) => Lum(a) > Lum(b)).All(x => x), "bathymetry colour gets darker with every step deeper");
        Check(Bathymetry.ColorAt(-50f).Equals(Bathymetry.ColorAt(0f)) && Bathymetry.ColorAt(20000f).Equals(Bathymetry.ColorAt(11000f)), "bathymetry colour clamps outside 0–11000 m");
        Check(probes.All(d => { var c = Bathymetry.ColorAt(d); return c.b >= c.r && c.b >= c.g * 0.9f; }), "bathymetry colours stay blue");
        Check(Sat(0f) < Sat(9000f) || Lum(0f) > Lum(9000f) * 3f, "shallow water is bright cyan, the abyss dark navy");

        // 世界地圖的海洋地形層：只在選到海洋層時畫（地表在外面會蓋住）
        string layerXml = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Defs", "World", "Ocean_Layer.xml"));
        foreach (string layerClass in new[] { "DepthRim.WorldDrawLayer_OceanTerrain", "DepthRim.WorldDrawLayer_OceanHills", "DepthRim.WorldDrawLayer_OceanBathymetry" })
        {
            Check(layerXml.Contains("<li>" + layerClass + "</li>") && DepthRimAssembly.GetType(layerClass) != null,
                $"the ocean layer draws {layerClass}");
        }
        Check(layerXml.Contains("<elevationString>{0}m</elevationString>"), "the ocean layer shows the seafloor depth as its elevation");
        Check(biomes.Count == Enum.GetValues(typeof(OceanBiome)).Length, $"exactly one BiomeDef per ocean biome ({biomes.Count})");
        XElement oceanBase = doc.Root.Elements("BiomeDef").FirstOrDefault(b => (string)b.Attribute("Name") == "DepthRim_OceanBiomeBase");
        Check(oceanBase != null, "biomes share the abstract ocean base");
        XElement weather = oceanBase?.Element("baseWeatherCommonalities");
        Check(weather != null && (string)weather.Attribute("Inherit") == "False" && weather.Elements().Select(e => e.Name.LocalName).SequenceEqual(new[] { "DepthRim_Undersea" }),
            "ocean biomes only have the undersea weather (the readout says undersea, not underground)");

        // 升降井到得了的生態區（淺海、變溫層）至少一個，深的生態區存在（留給潛艇）
        var tiers = biomes.Where(b => b.Descendants("depthTier").Any()).Select(b => Enum.Parse<DepthTier>(b.Descendants("depthTier").First().Value)).ToList();
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
