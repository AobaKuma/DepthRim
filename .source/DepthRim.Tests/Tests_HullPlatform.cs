using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

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
        foreach (XElement e in props.Elements())
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

        // 只能鋪在深淵上
        Check((string)platform.Element("terrainAffordanceNeeded") == "DepthRim_OpenAbyss", "laid only over the abyss");
        XDocument abyssDoc = XDocument.Load(Path.Combine(defs, "World", "Abyss_Terrain.xml"));
        XElement abyss = abyssDoc.Root.Elements("TerrainDef").Single(e => (string)e.Element("defName") == "DepthRim_Abyss");
        Check(abyss.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_OpenAbyss"), "the abyss provides the open-abyss affordance");
        foreach (XElement seabed in abyssDoc.Root.Elements("TerrainDef").Where(e => ((string)e.Attribute("ParentName")) == "DepthRim_SeabedBase"))
        {
            Check(seabed.Element("affordances")?.Elements("li").Any(e => e.Value == "DepthRim_OpenAbyss") != true, $"{(string)seabed.Element("defName")} is not open abyss");
        }
        var affordanceDefs = doc.Root.Elements("TerrainAffordanceDef").Select(e => (string)e.Element("defName")).ToList();
        Check(affordanceDefs.Contains("DepthRim_OpenAbyss") && affordanceDefs.Contains("DepthRim_HullPlatform"), "both affordances are defined");

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
