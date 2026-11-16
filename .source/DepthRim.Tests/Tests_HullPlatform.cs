using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;
using Verse;

// 潛艇船體平台（DepthRim_HullPlatform）：比照 DMSE 軌道平台的內容檢查。
static partial class Test
{
    static void ContentHullPlatform()
    {
        string defs = Path.Combine(ModRoot, "1.7", "Defs");
        XDocument doc = XDocument.Load(Path.Combine(defs, "Buildings", "HullPlatform.xml"));
        XElement platform = doc.Root.Elements("TerrainDef").Single(e => (string)e.Element("defName") == "DepthRim_HullPlatform");

        // 貼圖：主地板，以及邊框與側面（由 SectionLayer_HullPlatformProps 畫，比照 Odyssey 的 SubstructureProps）
        string tex = (string)platform.Element("texturePath");
        Check(TextureExists(tex), $"floor texture exists ({tex})");
        Check(platform.Element("spaceEdgeGraphicData") == null, "no vanilla spaceEdgeGraphicData (its side faces would cover the seabed)");
        XElement props = platform.Element("modExtensions").Elements("li").Single(e => (string)e.Attribute("Class") == "DepthRim.HullPlatformPropsExtension");
        foreach (XElement e in props.Elements().Where(x => x.Name.LocalName.Contains("TexPath")))
        {
            if (e.Name.LocalName == "sideLoopTexPaths")
            {
                Check(e.Elements("li").Count() == 5, "five side loop variants");
                foreach (XElement li in e.Elements("li")) { Check(TextureExists(li.Value), $"side loop texture exists ({li.Value})"); }
            }
            else
            {
                Check(TextureExists(e.Value), $"{e.Name.LocalName} exists ({e.Value})");
            }
        }
        foreach (string field in new[] { "oShapeTexPath", "uShapeTexPath", "cornerInnerTexPath", "cornerOuterTexPath", "flatTexPath", "sideLeftTexPath", "sideRightTexPath", "sideSingleTexPath" })
        {
            Check(props.Element(field) != null, $"props define {field}");
        }

        // 側面的段落與高度
        Check(HullPlatformProps.PieceFor(false, false) == HullPlatformProps.SidePiece.Single, "a lone edge cell gets the single piece");
        Check(HullPlatformProps.PieceFor(false, true) == HullPlatformProps.SidePiece.Left, "the west end of a run gets the left piece");
        Check(HullPlatformProps.PieceFor(true, false) == HullPlatformProps.SidePiece.Right, "the east end of a run gets the right piece");
        Check(HullPlatformProps.PieceFor(true, true) == HullPlatformProps.SidePiece.Loop, "the middle of a run loops");
        Check(HullPlatformProps.SideCells(true, true) == 2, "two cells of open abyss: the full side face");
        Check(HullPlatformProps.SideCells(true, false) == 1, "solid ground two cells down: only the top half");
        Check(HullPlatformProps.SideCells(false, true) == 0, "solid ground right below: no side face");

        // 海面地圖上浮在水面：只畫乾舷，貼圖取上方一段
        Near(HullPlatformProps.SideHeight(true, true, true, 0.5f), 0.5f, "on the sea surface a floating hull shows half a cell of freeboard");
        Near(HullPlatformProps.SideHeight(true, true, false, 0.5f), 2f, "over the abyss it still hangs two cells");
        Near(HullPlatformProps.SideHeight(true, false, false, 0.5f), 1f, "seabed two cells down: only the top half");
        Near(HullPlatformProps.SideHeight(true, true, true, 0f), 2f, "a raised platform (no freeboard) keeps its full side on the sea surface");
        Near(HullPlatformProps.SideHeight(false, true, true, 0.5f), 0f, "deck right below: no side face");
        Near(HullPlatformProps.SideHeight(false, false, false, 0.5f, onSeabed: true), 0.5f, "resting on the seabed with seabed to the south: half a cell of freeboard, like the sea surface");
        Near(HullPlatformProps.SideHeight(true, true, false, 0.5f, onSeabed: true), 2f, "resting on the seabed at an abyss edge: still hangs two cells");
        Near(HullPlatformProps.SideHeight(false, false, false, 0f, onSeabed: true), 0f, "a platform without freeboard shows no side on the seabed");
        Near(HullPlatformProps.SideHeight(false, false, false, 0.5f), 0f, "not on the seabed and ground to the south: no side face");

        // 觸底揚塵的外圈：船體外、地面、靠近觸底格
        var hull = new HashSet<IntVec3>();
        for (int x = 0; x < 4; x++) { for (int z = 0; z < 2; z++) { hull.Add(new IntVec3(10 + x, 0, 10 + z)); } }
        var none = MapComponent_TouchdownDust.RingCells(hull, new HashSet<IntVec3>(), c => true, 2);
        Check(none.Count == 0, "no contact with the seabed: no dust ring");
        var contact = new HashSet<IntVec3>(hull);
        var ring = MapComponent_TouchdownDust.RingCells(hull, contact, c => true, 2);
        Check(ring.Count == (4 + 4) * (2 + 4) - 8 && !ring.Any(hull.Contains), $"the ring surrounds the hull out to two cells ({ring.Count})");
        Check(ring.Distinct().Count() == ring.Count, "each ring cell once");
        var westOnly = MapComponent_TouchdownDust.RingCells(hull, contact, c => c.x < 10, 2);
        Check(westOnly.Count > 0 && westOnly.All(c => c.x < 10), "only ground cells raise dust (abyss to the east stays clear)");
        Near(HullPlatformProps.SideVMin(2f), 0f, "two cells use the whole side texture");
        Near(HullPlatformProps.SideVMin(1f), 0.5f, "one cell uses the top half");
        Near(HullPlatformProps.SideVMin(0.5f), 0.75f, "half a cell uses the top quarter");
        float freeboard = float.Parse((string)props.Element("seaSurfaceFreeboard"));
        Check(freeboard > 0f && freeboard < 1f, $"the hull platform floats with a low freeboard ({freeboard})");
        Near(MapComponent_SeaSurface.HullParams.x, freeboard, "the sea shader's waterline matches the freeboard");
        XDocument seaDoc = XDocument.Load(Path.Combine(defs, "SeaSurface", "SeaSurface.xml"));
        XElement oceanPlatform = seaDoc.Root.Elements("TerrainDef").Single(e => (string)e.Element("defName") == "DepthRim_OceanPlatform");
        Check(oceanPlatform.Element("modExtensions").Elements("li").Single(e => (string)e.Attribute("Class") == "DepthRim.HullPlatformPropsExtension").Element("seaSurfaceFreeboard") == null, "the ocean platform stands high above the sea (no freeboard)");
        string shader = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Shaders", "DepthRim_SeaSurface.frag.glsl"));
        Check(shader.Contains("vec4 _Hull;") && shader.Contains(".b"), "the sea shader reads the floating-hull mask channel");

        // 深淵背景做深度測試（海床崖壁的原版側面要留得住）
        XDocument shaders = XDocument.Load(Path.Combine(defs, "Rendering", "WaterVolume_Shader.xml"));
        XElement backdrop = shaders.Root.Elements("ShaderDef").Single(e => (string)e.Element("defName") == "DepthRim_Abyss").Element("shaderProgram");
        Check((string)backdrop.Element("depthTest") == "true", "the abyss backdrop depth-tests, so seabed cliff side faces stay visible");

        // 是基底地形：邊緣只畫在 BaseTerrainAt 上
        Check((string)platform.Element("layerable") == "false", "base terrain (not layerable), so its edges are drawn");
        Check(platform.Element("isFoundation") == null, "not a foundation");

        // 承載力：船體要蓋水密隔牆與水密艙門（Heavy）
        var gives = platform.Element("affordances").Elements("li").Select(e => e.Value).ToList();
        foreach (string a in new[] { "Light", "Medium", "Heavy", "Walkable", "DepthRim_HullPlatform" })
        {
            Check(gives.Contains(a), $"platform provides {a}");
        }
        XDocument flooding = XDocument.Load(Path.Combine(defs, "Flooding", "Flooding_Things.xml"));
        XElement door = flooding.Root.Elements("ThingDef").Single(e => (string)e.Element("defName") == "DepthRim_WatertightDoor");
        Check(gives.Contains((string)door.Element("terrainAffordanceNeeded")), "the watertight door can be built on the platform");

        // 鋪在開放水域或海床上：深淵、開放海面、海床
        Check((string)platform.Element("terrainAffordanceNeeded") == "DepthRim_HullBase", "laid over open water or the seabed");
        XDocument abyssDoc = XDocument.Load(Path.Combine(defs, "World", "Abyss_Terrain.xml"));
        XElement abyss = abyssDoc.Root.Elements("TerrainDef").Single(e => (string)e.Element("defName") == "DepthRim_Abyss");
        Check(abyss.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_OpenWater"), "the abyss is open water");
        Check(abyss.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_HullBase"), "the hull platform can be laid over the abyss");
        XElement openSea = XDocument.Load(Path.Combine(defs, "SeaSurface", "SeaSurface.xml")).Root.Elements("TerrainDef").Single(e => (string)e.Element("defName") == "DepthRim_OpenSea");
        Check(openSea.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_OpenWater"), "the open sea is open water");
        Check(openSea.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_HullBase"), "the hull platform can be laid over the open sea");
        XElement seabedBase = abyssDoc.Root.Elements("TerrainDef").Single(e => (string)e.Attribute("Name") == "DepthRim_SeabedBase");
        Check(seabedBase.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_HullBase"), "the hull platform can be laid on the seabed");
        foreach (XElement seabed in abyssDoc.Root.Elements("TerrainDef").Where(e => ((string)e.Attribute("ParentName")) == "DepthRim_SeabedBase"))
        {
            Check(seabed.Element("affordances")?.Elements("li").Any(e => e.Value == "DepthRim_OpenWater" || e.Value == "DepthRim_OpenAbyss") != true, $"{(string)seabed.Element("defName")} is not open water");
        }
        var affordanceDefs = doc.Root.Elements("TerrainAffordanceDef").Select(e => (string)e.Element("defName")).ToList();
        Check(affordanceDefs.Contains("DepthRim_OpenWater") && affordanceDefs.Contains("DepthRim_HullBase") && affordanceDefs.Contains("DepthRim_HullPlatform"), "the affordances are defined");

        // 拆除沿用 DMSE 的指令
        Check(platform.Element("modExtensions")?.Elements("li").Any(e => (string)e.Attribute("Class") == "DMSE.RemovablePlatformExtension") == true, "removable with DMSE's remove-platform command");
        Check(platform.Element("costList")?.Elements().Any() == true, "has a build cost (refunded on removal)");
    }

    static bool TextureExists(string path)
        => File.Exists(Path.Combine(ModRoot, "Textures", path.Replace('/', Path.DirectorySeparatorChar) + ".png"));

    // DepthRim 自己的建築分頁「水下」：所有可建造的 DepthRim 建築都放在這裡
    static void ContentDesignationCategory()
    {
        string defs = Path.Combine(ModRoot, "1.7", "Defs");
        XDocument cat = XDocument.Load(Path.Combine(defs, "Buildings", "DesignationCategory.xml"));
        XElement tab = cat.Root.Elements("DesignationCategoryDef").Single(e => (string)e.Element("defName") == "DepthRim_Underwater");
        var special = tab.Element("specialDesignatorClasses").Elements("li").Select(e => e.Value).ToList();
        Check(special.Contains("Designator_Cancel") && special.Contains("Designator_Deconstruct"), "the tab has cancel and deconstruct");
        Check(special.Contains("DMSE.Designator_RemovePlatform"), "the tab has DMSE's remove-platform tool (for hull platforms)");
        Check(int.Parse((string)tab.Element("order")) < 295 && int.Parse((string)tab.Element("order")) > 250, "sits between DMSE's BVR tab and Misc");

        string[] buildables = { "DepthRim_IceShaft", "DepthRim_BilgePump", "DepthRim_WatertightDoor", "DepthRim_PressureHullWall", "DepthRim_HullPlatform" };
        var all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).Where(e => e.Name.LocalName == "ThingDef" || e.Name.LocalName == "TerrainDef").ToList();
        foreach (string name in buildables)
        {
            XElement def = all.Single(e => (string)e.Element("defName") == name);
            Check((string)def.Element("designationCategory") == "DepthRim_Underwater", $"{name} is in the underwater tab");
        }
        Check(!all.Any(e => (string)e.Element("designationCategory") is string c && c != "DepthRim_Underwater"), "no DepthRim def is placed in another tab");
    }
}
