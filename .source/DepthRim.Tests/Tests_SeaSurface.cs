using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 海面地圖、海上平台與平台支柱。
static partial class Test
{
    static void ContentSeaSurface()
    {
        string defs = Path.Combine(ModRoot, "1.7", "Defs");
        XDocument doc = XDocument.Load(Path.Combine(defs, "SeaSurface", "SeaSurface.xml"));
        XElement Def(string type, string name) => doc.Root.Elements(type).Single(e => (string)e.Element("defName") == name);

        // 海不是原版深水：類虛空、不畫、不可通行
        XElement sea = Def("TerrainDef", "DepthRim_OpenSea");
        Check((string)sea.Element("dontRender") == "true", "open sea is not drawn as terrain (the backdrop layer draws it)");
        Check((string)sea.Element("passability") == "Impassable", "nothing stands on open sea");
        Check(sea.Element("texturePath") == null, "open sea has no terrain texture");
        Check(sea.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_OpenSea"), "open sea provides the open-sea affordance");

        // 平台層的高空：也不畫、不可通行；海上平台只能鋪在這裡（不在水面層）
        XElement air = Def("TerrainDef", "DepthRim_OpenAir");
        Check((string)air.Element("dontRender") == "true" && (string)air.Element("passability") == "Impassable", "open air is not drawn and cannot be stood on");
        Check(air.Element("affordances").Elements("li").Select(e => e.Value).SequenceEqual(new[] { "DepthRim_OpenAir" }), "open air only offers the open-air affordance (no hull platform up there)");
        Check(!sea.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_OpenAir"), "the water level is not open air");

        // 海上平台：只能鋪在平台層的高空，邊框與側面用 DR_OceanPlatform 貼圖
        XElement platform = Def("TerrainDef", "DepthRim_OceanPlatform");
        Check((string)platform.Element("terrainAffordanceNeeded") == "DepthRim_OpenAir", "ocean platform is laid only on the platform level");
        Check(TextureExists((string)platform.Element("texturePath")), "ocean platform floor texture exists");
        XElement props = platform.Element("modExtensions").Elements("li").Single(e => (string)e.Attribute("Class") == "DepthRim.HullPlatformPropsExtension");
        foreach (XElement e in props.Elements())
        {
            foreach (string path in e.Name.LocalName == "sideLoopTexPaths" ? e.Elements("li").Select(li => li.Value) : new[] { e.Value })
            {
                Check(TextureExists(path), $"ocean platform {e.Name.LocalName} exists ({path})");
            }
        }
        var gives = platform.Element("affordances").Elements("li").Select(e => e.Value).ToList();
        Check(gives.Contains("Heavy") && gives.Contains("Walkable"), "anything can be built on the ocean platform");
        Check(!gives.Contains("DepthRim_OpenSea"), "pillars cannot be built on top of the platform");

        // 平台支柱：蓋在水面層的開放海面上，是升降梯（MapPortal），頂端由程式生成在平台層
        XElement pillar = Def("ThingDef", "DepthRim_PlatformPillar");
        XElement graphic = pillar.Element("graphicData");
        Check((string)pillar.Element("thingClass") == "DepthRim.Building_PillarFoot", "the pillar is the sea-level elevator (Building_PillarFoot)");
        Check(typeof(RimWorld.MapPortal).IsAssignableFrom(typeof(Building_PillarFoot)), "the pillar foot is a map portal");
        Check(typeof(RimWorld.PocketMapExit).IsAssignableFrom(typeof(Building_PlatformPillar)), "the pillar top is the portal's exit");
        Check(TextureExists((string)graphic.Element("texPath")), "pillar base texture exists");
        Check((string)pillar.Element("size") == "(9,9)", "the pillar foot is 9x9");
        Check(pillar.Element("hasInteractionCell") == null && (string)pillar.Element("rotatable") == "false", "the pillar has no fixed interaction cell: its elevator spot is whichever side touches a floating hull");
        var landing = PlatformPillarGeometry.LandingCandidates(new Verse.IntVec3(10, 0, 10), new Verse.IntVec2(9, 9));
        Check(landing.Count == 36 && landing.Distinct().Count() == 36, "36 elevator spot candidates around the 9x9 (no corners)");
        Check(landing[0] == new Verse.IntVec3(10, 0, 5), "the middle of the south side is tried first");
        Check(landing.All(c => (System.Math.Abs(c.x - 10) == 5) != (System.Math.Abs(c.z - 10) == 5) && System.Math.Abs(c.x - 10) <= 5 && System.Math.Abs(c.z - 10) <= 5), "every candidate touches one side of the 9x9");
        var small = PlatformPillarGeometry.LandingCandidates(new Verse.IntVec3(10, 0, 10), new Verse.IntVec2(3, 3));
        Check(small.Count == 12 && small[0] == new Verse.IntVec3(10, 0, 8), "candidates follow the footprint size (3x3: 12, south middle first)");
        Check(pillar.Element("placeWorkers").Elements("li").Any(e => e.Value == "DepthRim.PlaceWorker_PillarFoot"), "a new pillar is checked on both levels");
        Check((string)pillar.Element("terrainAffordanceNeeded") == "DepthRim_OpenSea", "the pillar is built on open sea at the water level");
        Check((string)pillar.Element("drawerType") == "None", "the pillar foot itself is not drawn (its body is a background layer)");
        XElement portal = pillar.Element("portal");
        Check((string)portal.Element("pocketMapGenerator") == "DepthRim_PlatformLevel" && (string)portal.Element("exitDef") == "DepthRim_PlatformPillarTop", "the pillar leads to the platform level and its top");
        XElement top = Def("ThingDef", "DepthRim_PlatformPillarTop");
        Check((string)top.Element("thingClass") == "DepthRim.Building_PlatformPillar" && top.Element("designationCategory") == null, "the pillar top is not built by hand");
        Check((string)top.Element("size") == "(3,3)", "the pillar top is 3x3 too");
        Check(Building_PillarFoot.StartPlatformSize == 5, "a new pillar opens a 5x5 platform");
        XElement level = Def("MapGeneratorDef", "DepthRim_PlatformLevel");
        Check(level.Element("pocketMapProperties") != null && level.Element("genSteps").Elements("li").Single().Value == "DepthRim_PlatformLevel", "the platform level is a pocket map that starts as open air");
        Check(Def("GenStepDef", "DepthRim_SeaSurface").Element("genStep").Element("platformSize").Value == "(0,0)", "a new sea surface map is only sea");
        Check(platform.Element("placeWorkers").Elements("li").Any(e => e.Value == "DepthRim.PlaceWorker_PlatformSupported"), "platform can only be laid within pillar support");
        XElement topExt = top.Element("modExtensions").Elements("li").Single(e => (string)e.Attribute("Class") == "DepthRim.PlatformPillarExtension");
        XElement ext = pillar.Element("modExtensions").Elements("li").Single(e => (string)e.Attribute("Class") == "DepthRim.PlatformPillarExtension");
        Check(TextureExists((string)ext.Element("pillarTexPath")), "pillar body texture exists");
        Check((string)ext.Element("pillarDrawSize") == "(11,18.333)", "pillar body is drawn 11 wide");
        // 支撐由支柱頂算（平台層）；支柱腳的設定只用在水面層的柱身與放置時的範圍圈（柱身偏移可以各自調）
        float topRadius = float.Parse((string)topExt.Element("supportRadius"), System.Globalization.CultureInfo.InvariantCulture);
        Check(topRadius > 0f, $"the pillar top supports a radius ({topRadius})");
        Check(float.Parse((string)ext.Element("supportRadius"), System.Globalization.CultureInfo.InvariantCulture) <= topRadius, "the placing ring does not promise more than the top supports");
        Check((string)ext.Element("pillarTexPath") == (string)topExt.Element("pillarTexPath") && (string)ext.Element("pillarDrawSize") == (string)topExt.Element("pillarDrawSize"), "the foot and the top draw the same pillar body");

        // 繪製順序：海面 < 支柱 < 地形（2000）< 平台側面（Transparent 2900）
        int Queue(string name) => int.Parse((string)Def("ShaderDef", name).Element("shaderProgram").Element("renderQueue"));
        Check(Queue("DepthRim_SeaSurface") < Queue("DepthRim_Background"), "pillars draw over the sea");
        Check(Queue("DepthRim_Background") < 2000, "terrain (2000) draws over the pillars");
        // 平台底下的暗化層：水面層在建築與 Pawn（2900）之後、往上伸的柱身之前；平台層在往下看的快照之後、往下伸的柱身與甲板之前。
        // 柱身依高度自己算影子（同一個著色器、要世界座標）
        Check(Queue("DepthRim_PlatformShadow") > 2900 && Queue("DepthRim_PlatformShadow") < Queue("DepthRim_PillarRaised"), "the platform shadow darkens buildings and pawns under the deck; the raised pillar shades itself");
        Check(Queue("DepthRim_PlatformShadowLower") > Queue("DepthRim_BackgroundLower") && Queue("DepthRim_PlatformShadowLower") < Queue("DepthRim_Background"), "looking down, the shadow darkens the lower level; the pillar shades itself, the deck stays lit");
        foreach (string pillarShader in new[] { "DepthRim_Background", "DepthRim_PillarRaised" })
        {
            XElement program = Def("ShaderDef", pillarShader).Element("shaderProgram");
            Check((string)program.Element("fragmentShaderFileName") == "DepthRim_Pillar.frag.glsl" && (string)program.Element("vertexShaderFileName") == "DepthRim_Midwater.vert.glsl", $"{pillarShader} uses the height-shaded pillar shader with world coordinates");
        }
        string shaders = Path.Combine(ModRoot, "1.7", "Shaders");
        string shadowFrag = File.ReadAllText(Path.Combine(shaders, "DepthRim_PlatformShadow.frag.glsl"));
        string pillarFrag = File.ReadAllText(Path.Combine(shaders, "DepthRim_Pillar.frag.glsl"));
        string SharedBlock(string src)
        {
            int b = src.IndexOf('\n', src.IndexOf("// ---- platformShade begin"));
            int e = src.IndexOf("// ---- platformShade end");
            return b < 0 || e < 0 ? null : src.Substring(b, e - b).Replace("\r\n", "\n");
        }
        Check(SharedBlock(shadowFrag) != null && SharedBlock(shadowFrag) == SharedBlock(pillarFrag), "the shadow overlay and the pillar shader share the same platformShade code");
        Check(shadowFrag.Contains($"const int PILLAR_TAPS = {PlatformShadow.PillarTaps};"), "the shader's pillar-shadow taps match C#");
        Check(shadowFrag.Contains($"const int PILLAR_DEPTH_TAPS = {PlatformShadow.PillarDepthTaps};") && shadowFrag.Contains($"const float PILLAR_DEPTH = {PlatformShadow.PillarDepth:0.0};"), "the shader's pillar-shadow depth matches C#");
        int concrete = int.Parse((string)ext.Element("waterlineWidth"));
        Check(System.Math.Abs(PlatformShadow.PillarDepth - (concrete - 2)) < 0.01f, $"the pillar shadow is as thick as the pillar is wide (waterline 2 rows + {PlatformShadow.PillarDepth} = {concrete})");
        Check(shadowFrag.Contains("* vColor.a") && pillarFrag.Contains("* vColor.a"), "lamps light the shadow (per-vertex light)");
        string seaFrag = File.ReadAllText(Path.Combine(shaders, "DepthRim_SeaSurface.frag.glsl"));
        Check(!seaFrag.Contains("maskSoft") && !shadowFrag.Contains("maskSoft"), "the mask is blurred once on the CPU; shaders sample it once per lookup");
        Check(seaFrag.Contains("vec4 _Cast;") && seaFrag.Contains("u._Cast.xy"), "glints and the hull's own short shadow follow the sun");

        // 人造光照亮影子：原版燈具的地面亮度上限 0.5 時完全照亮
        Near(PlatformShadow.LitFraction(0f), 0f, "no lamp: full shadow");
        Near(PlatformShadow.LitFraction(0.25f), 0.5f, "a dim lamp lifts half of the shadow");
        Near(PlatformShadow.LitFraction(0.5f), 1f, "a lamp at full ground glow lights the shadow completely");
        Near(PlatformShadow.ShadedGlow(1f, 1f, 0f), 0f, "fully under the deck at noon, no lamp: 0% light");
        Near(PlatformShadow.ShadedGlow(1f, 1f, 0.5f), 0.5f, "a lamp still lights the shadow");
        Near(PlatformShadow.ShadedGlow(1f, 0f, 0f), 1f, "no shadow: full daylight");

        // 太陽：照原版建築影子，正午短、黃昏長（有上限），陰天淡
        UnityEngine.Vector2 noon = PlatformShadow.CastFromVanilla(new UnityEngine.Vector2(0f, -1.5f));
        Near(noon.x, 0f, "at noon the shadow falls straight south");
        Near(noon.y, -1.5f * MapComponent_SeaSurface.SeaDropCells * PlatformShadow.CastScale, "and is short");
        Near(PlatformShadow.CastFromVanilla(new UnityEngine.Vector2(15f, -7.125f)).magnitude, PlatformShadow.MaxCast, "at dusk it is long, but capped");
        Near(PlatformShadow.WeatherFactor(new UnityEngine.Color(0.718f, 0.745f, 0.757f)), 1f, "clear weather: full shadow", 0.03f);
        Check(PlatformShadow.WeatherFactor(new UnityEngine.Color(0.92f, 0.92f, 0.92f)) < 0.35f, "overcast (rain, fog): faint shadow");

        // 遮罩資料：一塊 10×10 的甲板（15..24）、一根支柱入水處（x 5..7、z 5..6）
        const int size = 40;
        bool[] raised = new bool[size * size];
        bool[] pillarCells = new bool[size * size];
        for (int z = 15; z < 25; z++)
        {
            for (int x = 15; x < 25; x++)
            {
                raised[z * size + x] = true;
            }
        }
        for (int z = 5; z <= 6; z++)
        {
            for (int x = 5; x <= 7; x++)
            {
                pillarCells[z * size + x] = true;
            }
        }
        var field = new PlatformShadowField(size, size, raised, pillarCells, new bool[size * size]);
        var noonSun = new PlatformSun(noon, 1f);
        var nightSun = new PlatformSun(noon, 0f);
        UnityEngine.Vector2 V(float x, float z) => new UnityEngine.Vector2(x, z);
        Near(field.GameplayShadow(V(20f, 20f), noonSun), 1f, "under the middle of the deck: no daylight at all", 0.05f);
        Near(field.GameplayShadow(V(3f, 35f), noonSun), 0f, "far from the deck: full daylight");
        float southEdgeDay = field.GameplayShadow(V(20f, 14f), noonSun);
        float southEdgeNight = field.GameplayShadow(V(20f, 14f), nightSun);
        Check(southEdgeDay > 0.5f && southEdgeNight < southEdgeDay, $"just south of the deck the noon sun is blocked; without sun only the soft sky shade remains ({southEdgeDay:F2} / {southEdgeNight:F2})");
        float eastDusk = field.Shade(V(28f, 20f), V(8f, -3f), 0f, 0f).x;
        float eastNoon = field.Shade(V(28f, 20f), noon, 0f, 0f).x;
        Check(eastDusk > 0.5f && eastNoon < 0.05f, $"an evening sun throws the shadow east of the deck ({eastDusk:F2} vs noon {eastNoon:F2})");
        Check(field.Shade(V(12.5f, 6f), V(6f, 0f), 0f, 0f).x > 0.5f && field.Shade(V(12.5f, 6f), noon, 0f, 0f).x < 0.05f, "a pillar throws a long, thin shadow along the sun");
        // 柱影的厚度＝柱子的粗細：遮罩只有入水處（9×2），影子要往北延伸到整個 9 格寬的圓柱斷面
        bool[] waterline = new bool[size * size];
        foreach (Verse.IntVec3 c in PlatformPillarGeometry.WaterlineCells(new Verse.IntVec3(20, 0, 5), 9))
        {
            waterline[c.z * size + c.x] = true;
        }
        var columnField = new PlatformShadowField(size, size, new bool[size * size], waterline, new bool[size * size]);
        UnityEngine.Vector2 westSun = V(-8f, 0f);
        float atWaterline = columnField.Shade(V(12f, 4.5f), westSun, 0f, 0f).x;
        float behindWaterline = columnField.Shade(V(12f, 10.5f), westSun, 0f, 0f).x;
        float pastPillar = columnField.Shade(V(12f, 15.5f), westSun, 0f, 0f).x;
        float inFront = columnField.Shade(V(12f, 1.5f), westSun, 0f, 0f).x;
        Check(atWaterline > 0.5f && behindWaterline > 0.5f, $"a low western sun casts a pillar shadow as thick as the pillar ({atWaterline:F2} at the waterline, {behindWaterline:F2} six cells behind it)");
        Check(pastPillar < 0.05f && inFront < 0.05f, $"but not thicker than the pillar ({pastPillar:F2} behind it, {inFront:F2} in front)");
        Check(field.Shade(V(20f, 20f), noon, 11f, 0f).x > field.Shade(V(20f, 30f), noon, 11f, 0f).x, "a pillar just under the deck is still shaded where the deck covers it");
        Near(field.Shade(V(20f, 20f), noon, MapComponent_SeaSurface.SeaDropCells + 1f, 0f).magnitude, 0f, "the pillar cap above the deck is not shaded");
        float hardEdge = field.Shade(V(20f, 13.5f), noon, 0f, 0f).x;
        float softEdge = field.Shade(V(20f, 13.5f), noon, 0f, PlatformShadow.LowerExtraSoftness).x;
        Check(System.Math.Abs(hardEdge - softEdge) > 0.01f, $"looking down from the platform the shadow edge is softer ({hardEdge:F2} vs {softEdge:F2})");
        UnityEngine.Color32[] px = field.ToPixels();
        Check(px[20 * size + 20].r > 240 && px[20 * size + 20].a > 150 && px[5 * size + 6].g > 80 && px[2 * size + 2].r == 0, "the uploaded mask carries the blurred deck (r, a) and pillar (g) channels");

        // 地圖生成：不用原版 Fog／玩家起點
        XElement gen = Def("MapGeneratorDef", "DepthRim_SeaSurface");
        Check(!gen.Element("genSteps").Elements("li").Any(e => e.Value == "Fog" || e.Value == "FindPlayerStartSpot"), "no vanilla fog or player start spot on the sea surface");
        Check((string)Def("WorldObjectDef", "DepthRim_SeaSurfaceSite").Element("worldObjectClass") == "DepthRim.WorldObject_SeaSurfaceSite", "sea surface site uses its own map parent");

        // 柱身與海面對齊：入水處＝海面在甲板下方的距離
        float topOffset = float.Parse((string)topExt.Element("pillarOffsetZ"), System.Globalization.CultureInfo.InvariantCulture);
        float drop = PlatformPillarGeometry.SeaDrop(topOffset, 18.333f);
        Near(drop, MapComponent_SeaSurface.SeaDropCells, "sea drop matches the pillar waterline", 0.05f);
        string frag = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Shaders", "DepthRim_SeaSurface.frag.glsl"));
        Check(frag.Contains("vec4(14.1,"), "the shader default sea drop matches too");
        UnityEngine.Rect body = PlatformPillarGeometry.PillarRect(10.5f, 20.5f, new UnityEngine.Vector2(11f, 18.333f), topOffset);
        Near(body.yMax - 20.5f, 1.67f, "pillar top sits 1.67 cells above the base centre", 0.01f);
        Near(body.xMin, 5f, "pillar body is centred on the base");

        // 支撐範圍（圓形，從基座中心算）
        UnityEngine.Vector3 center = new UnityEngine.Vector3(10.5f, 0f, 10.5f);
        Check(PlatformPillarGeometry.Supports(center, new Verse.IntVec3(28, 0, 10), 18f), "18 cells east is supported");
        Check(!PlatformPillarGeometry.Supports(center, new Verse.IntVec3(29, 0, 10), 18f), "19 cells east is not");
        Check(!PlatformPillarGeometry.Supports(center, new Verse.IntVec3(23, 0, 23), 18f), "the corner of the bounding square is outside the circle");

        // 柱身跨區塊時每個區塊只畫自己那一段
        UnityEngine.Rect section = UnityEngine.Rect.MinMaxRect(0f, 17f, 17f, 34f);
        Check(PlatformPillarGeometry.Clip(UnityEngine.Rect.MinMaxRect(5f, 10f, 16f, 28.333f), section, out UnityEngine.Rect quad, out UnityEngine.Rect uv), "a pillar crossing a section edge is clipped");
        Near(quad.yMin, 17f, "clipped quad starts at the section edge");
        Near(uv.yMin, 7f / 18.333f, "clipped uv starts at the matching texture row", 0.001f);
        Near(uv.yMax, 1f, "clipped uv keeps the pillar top");
        Check(!PlatformPillarGeometry.Clip(UnityEngine.Rect.MinMaxRect(5f, 0f, 16f, 10f), section, out _, out _), "a pillar outside the section is not drawn there");
        // 崩塌水花：每 5×5 一個放大的水花，而不是每格一個
        var strip = new System.Collections.Generic.List<Verse.IntVec3>();
        for (int x = 0; x < 15; x++) { for (int z = 0; z < 3; z++) { strip.Add(new Verse.IntVec3(x, 0, z)); } }
        var groups = PlatformPillarGeometry.SplashGroups(strip, 5);
        Check(groups.Count == 3 && groups.All(g => g.count == 15), $"a 15x3 strip makes three splash groups of 15 cells ({groups.Count})");
        Near(groups.OrderBy(g => g.center.x).First().center.x, 2.5f, "a splash sits at its group's centre");
        Near(PlatformPillarGeometry.SplashScale(1), 1f, "a lone cell splashes at normal size");
        Near(PlatformPillarGeometry.SplashScale(25), 2.5f, "a full 5x5 chunk splashes at 2.5x");
        Near(PlatformPillarGeometry.SplashScale(100), 2.5f, "splash scale is capped");
        Check(PlatformPillarGeometry.WaterlineCells(new Verse.IntVec3(10, 0, 10), 9).Count() == 18, "waterline foam covers 9x2 cells");
    }
}
