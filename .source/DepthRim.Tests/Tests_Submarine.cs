using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 潛艇（DMSE 轉移飛行換皮）：下潛規則、海路搜尋、深淵坑形狀與內容檢查。
static partial class Test
{
    static void SubmarineDive()
    {
        Check(DiveMath.Effective(DepthTier.Deep, DepthTier.Seabed) == DepthTier.Deep, "cruising above a deeper seafloor stays at the cruising depth");
        Check(DiveMath.Effective(DepthTier.Deep, DepthTier.Shallow) == DepthTier.Shallow, "over a shallow shelf the submarine is pushed up to the shallows");

        Check(DiveMath.CanSet(DepthTier.Deep, DepthTier.Shallow, DepthTier.Deep, DepthTier.Shallow) == DiveMath.DiveBlock.None, "surfacing is always allowed, even with a weak hull");
        Check(DiveMath.CanSet(DepthTier.Shallow, DepthTier.Shallow, DepthTier.Deep, DepthTier.Seabed) == DiveMath.DiveBlock.AlreadyThere, "the current target is not offered again");
        Check(DiveMath.CanSet(DepthTier.Shallow, DepthTier.Deep, DepthTier.Thermocline, DepthTier.Seabed) == DiveMath.DiveBlock.BelowSeafloor, "cannot dive below the seafloor");
        Check(DiveMath.CanSet(DepthTier.Shallow, DepthTier.Deep, DepthTier.Seabed, DepthTier.Thermocline) == DiveMath.DiveBlock.HullTooWeak, "cannot dive deeper than the hull rating");
        Check(DiveMath.CanSet(DepthTier.Shallow, DepthTier.Seabed, DepthTier.Seabed, DepthTier.Seabed) == DiveMath.DiveBlock.None, "a seabed-rated hull reaches the seabed");

        Check(DiveMath.Step(DepthTier.Shallow, DepthTier.Deep) == DepthTier.Thermocline, "diving goes one tier at a time");
        Check(DiveMath.Step(DepthTier.Seabed, DepthTier.Shallow) == DepthTier.Deep, "surfacing goes one tier at a time");
        Check(DiveMath.Step(DepthTier.Deep, DepthTier.Deep) == DepthTier.Deep, "no step when already there");
        Check(DiveMath.TravelTicks(DepthTier.Shallow, DepthTier.Seabed) == 3 * DiveMath.TicksPerTier, "three tiers take three hours");

        Check(HullPressure.RatingOf(null) == DepthTier.Shallow, "unknown hull pieces count as shallow-rated");

        // 航行推進：推力不足時航程時鐘往後推
        Near(VoyageMath.ThrustFraction(3f, 3f), 1f, "full thrust");
        Near(VoyageMath.ThrustFraction(1.5f, 3f), 0.5f, "half thrust");
        Near(VoyageMath.ThrustFraction(0f, 3f), 0f, "no thrust");
        Near(VoyageMath.ThrustFraction(9f, 3f), 1f, "never faster than planned");
        Near(VoyageMath.ThrustFraction(0f, 0f), 1f, "no planned thrust never stalls forever");
        float carry = 0f;
        Check(VoyageMath.StallTicks(60, 1f, ref carry) == 0, "full thrust never stalls");
        Check(VoyageMath.StallTicks(60, 0f, ref carry) == 60, "no thrust stalls the whole interval");
        carry = 0f;
        int total = 0;
        for (int i = 0; i < 10; i++) { total += VoyageMath.StallTicks(15, 0.5f, ref carry); }
        Check(total == 75, $"half thrust stalls half the time, with the fractions carried over ({total})");
        carry = 0f;
        int thirds = 0;
        for (int i = 0; i < 300; i++) { thirds += VoyageMath.StallTicks(1, 2f / 3f, ref carry); }
        Check(thirds >= 99 && thirds <= 100, $"fractional stalls add up over many ticks ({thirds})");

        // 航程耗電：沒有出航預扣，推進器在航行中的耗電就是成本
        Near(VoyageMath.EnergyWd(150f, 24f), 150f, "150 W for a day is 150 Wd");
        Near(VoyageMath.EnergyWd(300f, 6f), 75f, "300 W for six hours is 75 Wd");
        Near(VoyageMath.EnergyWd(0f, 24f), 0f, "no draw costs nothing");
        Check(VoyageMath.RangeTiles(600f, 150f, 6f, 4f) == 22, $"600 Wd runs 150 W for 96 h: 6 h of burns, then 22 tiles at 4 h each ({VoyageMath.RangeTiles(600f, 150f, 6f, 4f)})");
        Check(VoyageMath.RangeTiles(10f, 150f, 6f, 4f) == -1, "not even enough for the burns: cannot depart");
        Check(VoyageMath.RangeTiles(37.5f, 150f, 6f, 4f) == 0, "exactly enough for the burns: zero tiles");
        Check(VoyageMath.RangeTiles(600f, 0f, 6f, 4f) == int.MaxValue, "no draw: unlimited range");
        Check(VoyageMath.RangeTiles(600f, 300f, 6f, 4f) < VoyageMath.RangeTiles(600f, 150f, 6f, 4f), "more draw, less range");
        Check(VoyageMath.StandbyFactor > 0f && VoyageMath.StandbyFactor < 1f, "thrusters idle at a fraction of their draw");

        // 溫躍層遮蔽：兩側打對折、躲在層裡打七五折
        Near(SonarMath.LayerFactor(DepthTier.Shallow, DepthTier.Shallow), 1f, "same layer: full range");
        Near(SonarMath.LayerFactor(DepthTier.Deep, DepthTier.Seabed), 1f, "both below the thermocline: full range");
        Near(SonarMath.LayerFactor(DepthTier.Shallow, DepthTier.Deep), SonarMath.AcrossLayerFactor, "across the thermocline: half range");
        Near(SonarMath.LayerFactor(DepthTier.Seabed, DepthTier.Shallow), SonarMath.AcrossLayerFactor, "across the thermocline, looking up: half range");
        Near(SonarMath.LayerFactor(DepthTier.Shallow, DepthTier.Thermocline), SonarMath.InLayerFactor, "a target hiding in the layer");
        Near(SonarMath.LayerFactor(DepthTier.Thermocline, DepthTier.Deep), SonarMath.InLayerFactor, "listening from inside the layer");
        Near(SonarMath.LayerFactor(DepthTier.Thermocline, DepthTier.Thermocline), 1f, "both in the layer: full range");
        Check(SonarMath.AcrossLayerFactor < SonarMath.InLayerFactor && SonarMath.InLayerFactor < 1f, "across the layer hides more than inside it");

        // 下錨：只在淺海或觸底的海床，而且要正停在那個深度
        Check(DiveMath.Anchorable(DepthTier.Shallow) && DiveMath.Anchorable(DepthTier.Seabed), "the shallows and the seabed have a bottom to anchor on");
        Check(!DiveMath.Anchorable(DepthTier.Thermocline) && !DiveMath.Anchorable(DepthTier.Deep), "the thermocline and the deep have no bottom to anchor on");
        Check(DiveMath.CanAnchor(DepthTier.Shallow, DepthTier.Shallow, DepthTier.Shallow) == DiveMath.AnchorBlock.None, "resting in the shallows: can anchor");
        Check(DiveMath.CanAnchor(DepthTier.Seabed, DepthTier.Seabed, DepthTier.Seabed) == DiveMath.AnchorBlock.None, "resting on the seabed: can anchor");
        Check(DiveMath.CanAnchor(DepthTier.Deep, DepthTier.Deep, DepthTier.Deep) == DiveMath.AnchorBlock.NoBottom, "over a deep plain: nothing to anchor on");
        Check(DiveMath.CanAnchor(DepthTier.Deep, DepthTier.Deep, DepthTier.Seabed) == DiveMath.AnchorBlock.NotAtBottom, "above the seabed: dive down first");
        Check(DiveMath.CanAnchor(DepthTier.Shallow, DepthTier.Thermocline, DepthTier.Shallow) == DiveMath.AnchorBlock.NotAtBottom, "while changing depth: cannot anchor");
    }

    static void SubmarineRouting()
    {
        // 一條 0-1-2-3-4 的鏈，另有 0-5-4 的捷徑但 5 是陸地：路線只能走鏈
        int[][] edges = { new[] { 1, 5 }, new[] { 0, 2 }, new[] { 1, 3 }, new[] { 2, 4 }, new[] { 3, 5 }, new[] { 0, 4 } };
        OceanRouting.Flood f = OceanRouting.Bfs(6, 0, (i, list) => list.AddRange(edges[i]), i => i != 5);
        Check(f.dist[4] == 4, $"the route goes around land ({f.dist[4]})");
        Check(f.dist[5] == OceanRouting.Unreachable, "land is never entered");
        List<int> path = OceanRouting.PathTo(f, 0, 4);
        Check(path != null && path.SequenceEqual(new[] { 0, 1, 2, 3, 4 }), "the path follows the sea: " + (path == null ? "null" : string.Join(",", path)));
        Check(OceanRouting.PathTo(f, 0, 5) == null, "no path onto land");
        OceanRouting.Flood g = OceanRouting.Bfs(6, 0, (i, list) => list.AddRange(edges[i]), i => true);
        Check(g.dist[4] == 2, "without land in the way the short cut is used");
        Check(OceanRouting.PathTo(f, 0, 0).SequenceEqual(new[] { 0 }), "a path to the start is the start");
    }

    static void SubmarinePockets()
    {
        Check(GenStep_AbyssCarve.InPocket(0f, 10f, 0f), "the pocket centre is always carved");
        Check(GenStep_AbyssCarve.InPocket(7.9f, 10f, 0f), "inside 0.8 radius is always carved");
        Check(!GenStep_AbyssCarve.InPocket(8.1f, 10f, 0f), "low noise pulls the edge in");
        Check(GenStep_AbyssCarve.InPocket(11.9f, 10f, 1f), "high noise pushes the edge out");
        Check(!GenStep_AbyssCarve.InPocket(12.1f, 10f, 1f), "never beyond 1.2 radius");
    }

    static void ContentSubmarine()
    {
        string defs = Path.Combine(ModRoot, "1.7", "Defs");
        var all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        XElement Def(string type, string name) => all.Single(e => e.Name.LocalName == type && (string)e.Element("defName") == name);

        // 海洋層可以起飛：虛空 = 深淵，船體地圖與潛艇世界物件都是 DepthRim 的
        XElement ext = Def("PlanetLayerDef", "DepthRim_Ocean").Element("modExtensions").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.TransferEnvironmentExtension");
        Check((string)ext.Element("voidTerrain") == "DepthRim_Abyss", "the ocean's void is the abyss");
        Check((string)ext.Element("shipMapGenerator") == "DepthRim_SubmarineHull", "submarine maps are generated as plain abyss");
        Check((string)ext.Element("shipWorldObject") == "DepthRim_Submarine", "the ship world object is the submarine");
        Check((string)Def("WorldObjectDef", "DepthRim_Submarine").Element("worldObjectClass") == "DepthRim.WorldObject_Submarine", "the submarine world object uses WorldObject_Submarine");
        Check(Def("MapGeneratorDef", "DepthRim_SubmarineHull").Element("genSteps").Elements("li").Select(e => e.Value).SequenceEqual(new[] { "DepthRim_FillAbyss" }), "the submarine map only fills abyss");

        // 龍骨樁：只算船體平台，只能蓋在船體平台上
        foreach (string keel in new[] { "DepthRim_Keel", "DepthRim_KeelSmall" })
        {
            XElement d = Def("ThingDef", keel);
            XElement comp = d.Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.CompProperties_StructuralPile");
            Check(comp.Element("allowedTerrains").Elements("li").Select(e => e.Value).SequenceEqual(new[] { "DepthRim_HullPlatform" }), $"{keel}: only hull platform counts as hull (the seabed stays behind)");
            Check((string)comp.Element("spaceMapOnly") == "true", $"{keel}: only on maps that allow transfers");
            Check(TextureExists((string)d.Element("graphicData").Element("texPath")), $"{keel} texture exists");
        }
        XElement keelBase = all.Single(e => (string)e.Attribute("Name") == "DepthRim_KeelBase");
        Check((string)keelBase.Element("terrainAffordanceNeeded") == "DepthRim_HullPlatform", "keel frames are built on hull platforms");

        // 艦橋：DMSE 控制台 + DepthRim 下潛控制
        XElement helm = Def("ThingDef", "DepthRim_Helm");
        var helmComps = helm.Element("comps").Elements("li").Select(e => (string)e.Attribute("Class")).ToList();
        Check(helmComps.Contains("DMSE.CompProperties_TransferConsole") && helmComps.Contains("DepthRim.CompProperties_DiveControl"), "the helm is a transfer console with dive control");
        foreach (string dir in new[] { "north", "east", "south", "west" })
        {
            Check(TextureExists((string)helm.Element("graphicData").Element("texPath") + "_" + dir), $"helm texture ({dir}) exists");
        }

        // 推進器：沒有尾焰區、不噴火、要接電
        foreach (string thruster in new[] { "DepthRim_Thruster", "DepthRim_AdvancedThruster" })
        {
            XElement d = Def("ThingDef", thruster);
            XElement engine = d.Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.CompProperties_TransferEngine");
            Check((string)engine.Element("exclusionAreaSize") == "(0, 0, 0)", $"{thruster}: no exhaust danger zone");
            Check((string)engine.Element("flameSize") == "0", $"{thruster}: no flame");
            Check(d.Element("comps").Elements("li").Any(e => (string)e.Attribute("Class") == "CompProperties_Power"), $"{thruster}: needs power");
            Check(d.Element("comps").Elements("li").Any(e => (string)e.Attribute("Class") == "DepthRim.CompProperties_ThrusterDraw"), $"{thruster}: full draw only while under way");
            Check(TextureExists((string)d.Element("graphicData").Element("texPath")), $"{thruster} texture exists");
        }

        // 研究：每個建築由對應的研究解鎖（只看 ThingDef／TerrainDef，避免撞名的承載力 Def）
        var gates = new Dictionary<string, string>
        {
            { "DepthRim_IceShaft", "DepthRim_UnderwaterSurvival" },
            { "DepthRim_BilgePump", "DepthRim_UnderwaterSurvival" },
            { "DepthRim_WatertightDoor", "DepthRim_UnderwaterSurvival" },
            { "DepthRim_PressureHullWall", "DepthRim_PressureEngineering" },
            { "DepthRim_HullPlatform", "DepthRim_PressureEngineering" },
            { "DepthRim_Keel", "DepthRim_SubmarineConstruction" },
            { "DepthRim_KeelSmall", "DepthRim_SubmarineConstruction" },
            { "DepthRim_Helm", "DepthRim_SubmarineConstruction" },
            { "DepthRim_Thruster", "DepthRim_SubmarineConstruction" },
            { "DepthRim_AdvancedThruster", "DepthRim_AbyssalNavigation" },
        };
        foreach (var kv in gates)
        {
            XElement d = all.Single(e => (e.Name.LocalName == "ThingDef" || e.Name.LocalName == "TerrainDef") && (string)e.Element("defName") == kv.Key);
            Check(d.Element("researchPrerequisites")?.Elements("li").Any(e => e.Value == kv.Value) == true, $"{kv.Key} is unlocked by {kv.Value}");
        }
        Check((string)Def("ThingDef", "DepthRim_DivingSuit").Element("recipeMaker").Element("researchPrerequisite") == "DepthRim_UnderwaterSurvival", "the diving suit recipe needs underwater survival");
        Check(Def("ResearchProjectDef", "DepthRim_UnderwaterSurvival").Element("prerequisites").Elements("li").Any(e => e.Value == "Electricity"), "underwater survival needs electricity");

        // 聲納：DMSE 搜索雷達換皮。主動聲納範圍大、會暴露；被動聲納範圍小、不會被歸向魚雷找到
        XElement Radar(string name) => Def("ThingDef", name).Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.CompProperties_SearchRadar");
        XElement active = Radar("DepthRim_ActiveSonar");
        XElement passive = Radar("DepthRim_PassiveSonar");
        Check((string)active.Element("immuneToAntiRadiationSeeker") != "true", "active sonar gives its position away");
        Check((string)passive.Element("immuneToAntiRadiationSeeker") == "true", "passive sonar cannot be found by homing torpedoes");
        Check(float.Parse((string)active.Element("worldDetectionRange")) > float.Parse((string)passive.Element("worldDetectionRange")), "active sonar hears farther than passive sonar");
        XElement sonarBase = all.Single(e => (string)e.Attribute("Name") == "DepthRim_SonarBase");
        Check((string)sonarBase.Element("designationCategory") == "DepthRim_Underwater", "sonar is in the underwater build tab");
        Check(sonarBase.Element("researchPrerequisites").Elements("li").Any(e => e.Value == "DepthRim_UnderwaterCombat"), "sonar is unlocked by underwater combat");
        foreach (string sonar in new[] { "DepthRim_ActiveSonar", "DepthRim_PassiveSonar" })
        {
            XElement d = Def("ThingDef", sonar);
            Check(d.Element("comps").Elements("li").Any(e => (string)e.Attribute("Class") == "CompProperties_Power"), $"{sonar}: needs power");
            Check((string)d.Attribute("ParentName") == "DepthRim_SonarBase", $"{sonar}: built on the sonar base");
            Check(TextureExists((string)d.Element("graphicData").Element("texPath")), $"{sonar} texture exists");
        }
        Check(Def("ResearchProjectDef", "DepthRim_UnderwaterCombat").Element("prerequisites").Elements("li").Any(e => e.Value == "DepthRim_SubmarineConstruction"), "underwater combat follows submarine construction");
        string research = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Languages", "ChineseTraditional (繁體中文)", "DefInjected", "ResearchProjectDef", "DepthRim_Research.xml"));
        Check(research.Contains("<DepthRim_UnderwaterCombat.label>"), "underwater combat is translated");

        // 魚雷：DMSE 飛彈換皮。彈體帶魚雷標記與深度行為，部件只配魚雷彈體，發射管只裝魚雷
        XElement body = Def("DMSE.MissileBodyDef", "DepthRim_TorpedoBody");
        Check(body.Element("modExtensions").Elements("li").Any(e => (string)e.Attribute("Class") == "DepthRim.TorpedoExtension"), "the torpedo body is marked as a torpedo (sea-only)");
        Check(body.Element("behaviors").Elements("li").Any(e => (string)e.Attribute("Class") == "DepthRim.TorpedoBehaviorProps"), "the torpedo body carries its depth");
        Check(float.Parse((string)body.Element("baseWorldSpeedFactor")) < 0.75f, "torpedoes are slower than a light missile");
        float range = float.Parse((string)body.Element("baseFuel")) * float.Parse((string)body.Element("baseSpecificImpulse")) * float.Parse((string)body.Element("rangePerFuelImpulse"));
        Check(range >= 6f && range <= 20f, $"torpedo range is a handful of sea tiles ({range})");
        foreach (XElement part in body.Element("defaultParts").Elements("li"))
        {
            XElement pd = Def("DMSE.MissilePartDef", part.Value);
            Check(pd.Element("compatibleBodies").Elements("li").Any(e => e.Value == "DepthRim_TorpedoBody"), $"default part {part.Value} fits the torpedo body");
        }
        XElement homing = Def("DMSE.MissilePartDef", "DepthRim_Torpedo_Guidance_PassiveHoming");
        Check((string)homing.Element("guidanceType").Attribute("Class") == "DMSE.GuidanceType_AntiRadiation", "passive homing seeks active sonar (anti-radiation)");
        Check((string)Def("DMSE.MissilePartDef", "DepthRim_Torpedo_Payload_Noisemaker").Element("chaffCharges") == "1", "the acoustic decoy breaks one lock");
        XElement torpedo = Def("ThingDef", "DepthRim_Torpedo");
        Check((string)torpedo.Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.CompProperties_MissileConfig").Element("defaultBody") == "DepthRim_TorpedoBody", "the torpedo item uses the torpedo body");
        Check((string)torpedo.Element("recipeMaker").Element("researchPrerequisite") == "DepthRim_UnderwaterCombat", "torpedoes are made after underwater combat");
        XElement tube = Def("ThingDef", "DepthRim_TorpedoTube");
        Check(tube.Element("building").Element("fixedStorageSettings").Descendants("li").Select(e => e.Value).OrderBy(v => v)
            .SequenceEqual(new[] { "DepthRim_AntiTorpedo", "DepthRim_Torpedo" }), "the torpedo tube only holds torpedoes and anti-torpedo torpedoes");
        Check(tube.Element("comps").Elements("li").Any(e => (string)e.Attribute("Class") == "DMSE.CompProperties_MissileLauncher"), "the tube also fires interceptors (anti-torpedo torpedoes)");
        XElement rail = tube.Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.CompProperties_MissileRailLauncher");
        Check((string)rail.Element("missileBody") == "DepthRim_TorpedoBody" && (string)rail.Element("worldObjectDef") == "DepthRim_WorldObject_Torpedo", "the tube fires torpedo tracks");
        Check(tube.Element("researchPrerequisites").Elements("li").Any(e => e.Value == "DepthRim_UnderwaterCombat"), "the torpedo tube is unlocked by underwater combat");
        Check((string)tube.Element("designationCategory") == "DepthRim_Underwater", "the torpedo tube is in the underwater build tab");
        string zhRoot = Path.Combine(ModRoot, "1.7", "Languages", "ChineseTraditional (繁體中文)", "DefInjected");
        Check(File.ReadAllText(Path.Combine(zhRoot, "DMSE.MissilePartDef", "DepthRim_Torpedo.xml")).Contains("<DepthRim_Torpedo_Guidance_PassiveHoming.label>"), "torpedo parts are translated");
        Check(File.ReadAllText(Path.Combine(zhRoot, "DMSE.MissileBodyDef", "DepthRim_Torpedo.xml")).Contains("<DepthRim_TorpedoBody.label>"), "the torpedo body is translated");

        // 5.7 反魚雷：反魚雷魚雷（攔截用途、標記彈體、比魚雷快）、火控聲納、近防砲、敵方魚雷艇與事件
        XElement att = Def("DMSE.MissileBodyDef", "DepthRim_AntiTorpedoBody");
        Check(att.Element("modExtensions").Elements("li").Any(e => (string)e.Attribute("Class") == "DepthRim.AntiTorpedoExtension"), "the anti-torpedo body is marked");
        Check(float.Parse((string)att.Element("baseWorldSpeedFactor")) > float.Parse((string)body.Element("baseWorldSpeedFactor")), "anti-torpedo torpedoes outrun torpedoes");
        foreach (XElement part in att.Element("defaultParts").Elements("li"))
        {
            Check(Def("DMSE.MissilePartDef", part.Value).Element("compatibleBodies").Elements("li").Any(e => e.Value == "DepthRim_AntiTorpedoBody"), $"default part {part.Value} fits the anti-torpedo body");
        }
        XElement attItem = Def("ThingDef", "DepthRim_AntiTorpedo");
        Check(attItem.Descendants("role").Any(e => e.Value == "Interceptor"), "anti-torpedo torpedoes are interceptors only");
        Check((string)attItem.Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.CompProperties_MissileConfig").Element("defaultBody") == "DepthRim_AntiTorpedoBody", "the anti-torpedo item uses its body");
        Check(torpedo.Descendants("role").Any(e => e.Value == "Strike"), "attack torpedoes are never fired as interceptors");
        XElement fcs = Def("ThingDef", "DepthRim_FireControlSonar");
        Check((string)fcs.Attribute("ParentName") == "DepthRim_SonarBase", "the fire-control sonar is built on the sonar base");
        Check(fcs.Element("comps").Elements("li").Any(e => (string)e.Attribute("Class") == "DMSE.CompProperties_FireControlRadar"), "the fire-control sonar is a DMSE fire-control radar");
        XElement gun = Def("ThingDef", "DepthRim_SupercavitatingGun");
        XElement pointDef = gun.Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DepthRim.CompProperties_TorpedoPointDefense");
        float pdHit = float.Parse((string)pointDef.Element("hitChance"));
        Check(pdHit > 0f && pdHit < 1f, $"point defence is a chance, not a guarantee ({pdHit})");
        Check(gun.Element("comps").Elements("li").Any(e => (string)e.Attribute("Class") == "CompProperties_Refuelable"), "the point-defence gun uses ammunition");
        Check(gun.Element("researchPrerequisites").Elements("li").Any(e => e.Value == "DepthRim_UnderwaterCombat"), "the point-defence gun is unlocked by underwater combat");
        XElement boat = Def("WorldObjectDef", "DepthRim_EnemyTorpedoBoat");
        XElement baseComp = boat.Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.CompProperties_MissileBase");
        Check((string)baseComp.Element("missileBody") == "DepthRim_TorpedoBody" && (string)Def("WorldObjectDef", (string)baseComp.Element("incomingWorldObjectDef")).Element("worldObjectClass") == "DMSE.WorldObject_IncomingMissile", "the enemy boat fires torpedo tracks of the class DMSE's missile base creates");
        Check(int.Parse((string)baseComp.Element("range")) <= range, "the enemy boat's range does not exceed the torpedo's");
        // DMSE 擴充點（DMSE_調整計畫.md）：陣地的部件選項、離開信件、魚雷的水下領域與交戰判定、火控聲納的領域、海洋層的轉移 worker
        foreach (string enemyDef in new[] { "DepthRim_EnemyTorpedoBoat", "DepthRim_PirateSubmarine" })
        {
            XElement mb = Def("WorldObjectDef", enemyDef).Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.CompProperties_MissileBase");
            List<string> guidanceParts = mb.Element("partOptions").Elements("li").Where(o => (string)o.Element("category") == "Guidance")
                .SelectMany(o => o.Element("parts").Elements("li")).Select(e => e.Value).ToList();
            Check(guidanceParts.Count >= 2 && guidanceParts.All(n => Def("DMSE.MissilePartDef", n).Element("compatibleBodies").Elements("li").Any(b => b.Value == "DepthRim_TorpedoBody")),
                $"{enemyDef} picks a torpedo seeker from DMSE partOptions ({guidanceParts.Count})");
            string labelKey = (string)mb.Element("relocatedLetterLabelKey");
            Check(labelKey != null && File.ReadAllText(Path.Combine(ModRoot, "1.7", "Languages", "English", "Keyed", "DepthRim.xml")).Contains("<" + labelKey + ">"),
                $"{enemyDef} has its own 'left the area' letter ({labelKey})");
            Check((string)Def("WorldObjectDef", (string)mb.Element("incomingWorldObjectDef")).Element("worldObjectClass") == "DMSE.WorldObject_IncomingMissile",
                $"{enemyDef} fires the world object class DMSE's missile base needs");
        }
        Check((string)body.Element("domain") == "Underwater" && (string)body.Element("engageWorker")?.Attribute("Class") == "DepthRim.TorpedoEngageWorker",
            "torpedoes are underwater-domain and only travel by sea (DMSE engageWorker)");
        Check((string)Def("DMSE.MissileBodyDef", "DepthRim_AntiTorpedoBody").Element("domain") == "Underwater", "anti-torpedo torpedoes only engage the underwater domain");
        Check(Def("ThingDef", "DepthRim_FireControlSonar").Descendants("domains").Elements("li").Select(e => e.Value).SequenceEqual(new[] { "Underwater" }),
            "the fire-control sonar only guides underwater interceptors");
        Check(Def("ThingDef", "DepthRim_BoardingCraftIncoming").Descendants("domain").Any(e => e.Value == "Underwater"), "an incoming boarding craft is an underwater target");
        Check((string)Def("WorldObjectDef", "DepthRim_PirateSubmarine").Descendants("ignoreNetworkResearch").Single() == "true",
            "pirate subs defend themselves without DMSE's radar network research");
        XElement env = Def("PlanetLayerDef", "DepthRim_Ocean").Element("modExtensions").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.TransferEnvironmentExtension");
        Check((string)env.Element("routeWorker")?.Attribute("Class") == "DepthRim.OceanRouteWorker"
            && (string)env.Element("energyWorker")?.Attribute("Class") == "DepthRim.SubmarineEnergyWorker"
            && (string)env.Element("landingWorker")?.Attribute("Class") == "DepthRim.SubmarineLandingWorker",
            "the ocean layer routes, charges and lands submarines through DMSE's transfer workers");
        Check((string)env.Element("voidTerrain") == "DepthRim_Abyss", "removed hull platforms leave the abyss (DMSE reads voidTerrain)");
        XElement incident = Def("IncidentDef", "DepthRim_EnemyTorpedoBoat");
        string seaDist = (string)incident.Descendants("seaDistance").Single();
        Check(int.Parse(seaDist.Split('~')[1]) <= int.Parse((string)baseComp.Element("range")), $"the boat spawns within its own range ({seaDist})");
        Check((string)incident.Element("workerClass") == "DepthRim.IncidentWorker_EnemyTorpedoBoat", "the incident places the enemy boat");
        Check(File.ReadAllText(Path.Combine(zhRoot, "IncidentDef", "DepthRim_Incidents.xml")).Contains("<DepthRim_EnemyTorpedoBoat.letterText>"), "the enemy boat letter is translated");
        Check(File.ReadAllText(Path.Combine(zhRoot, "WorldObjectDef", "DepthRim_WorldObjects.xml")).Contains("<DepthRim_EnemyTorpedoBoat.label>"), "the enemy boat is translated");
        Check(File.ReadAllText(Path.Combine(zhRoot, "DMSE.MissileBodyDef", "DepthRim_Torpedo.xml")).Contains("<DepthRim_AntiTorpedoBody.label>"), "the anti-torpedo body is translated");

        // M6 跳幫突擊艇：降落不破屋頂、離場反向、DMS 運輸艇貼圖、只在海洋層、說書人元件
        XElement craftDef = Def("ThingDef", "DepthRim_BoardingCraft");
        Check((string)craftDef.Element("thingClass") == "DepthRim.Building_BoardingCraft", "the boarding craft uses its own building class");
        Check(TextureExists((string)craftDef.Element("graphicData").Element("texPath")), "the boarding craft texture exists");
        Check((string)craftDef.Element("building").Element("claimable") == "false", "players cannot claim an enemy boarding craft");
        XElement incoming = Def("ThingDef", "DepthRim_BoardingCraftIncoming");
        Check((string)incoming.Element("skyfaller").Element("hitRoof") == "false", "the incoming craft does not break the roof (keeps the compartment sealed)");
        Check((string)incoming.Element("size") == (string)craftDef.Element("size"), "the incoming skyfaller matches the craft's footprint");
        XElement leaving = Def("ThingDef", "DepthRim_BoardingCraftLeaving");
        Check((string)leaving.Element("skyfaller").Element("reversed") == "true" && (string)leaving.Element("thingClass") == "DepthRim.BoardingCraftLeaving", "the leaving craft flies up and takes its crew");
        foreach (string layered in new[] { "PawnsArrivalModeDef/DepthRim_BoardingArrival", "RaidStrategyDef/DepthRim_Boarding", "IncidentDef/DepthRim_BoardingRaid" })
        {
            string[] parts = layered.Split('/');
            Check(Def(parts[0], parts[1]).Element("layerWhitelist").Elements("li").Any(e => e.Value == "DepthRim_Ocean"), $"{parts[1]} is whitelisted for the ocean layer");
        }
        Check(Def("RaidStrategyDef", "DepthRim_Boarding").Element("arriveModes").Elements("li").Single().Value == "DepthRim_BoardingArrival", "boarding raids arrive by boarding craft only");
        XElement raidDef = Def("IncidentDef", "DepthRim_BoardingRaid");
        Check((string)raidDef.Element("baseChance") == "0", "vanilla storyteller cycles never pick boarding raids (the DepthRim comp schedules them)");
        Check(raidDef.Element("targetTags").Elements("li").Any(e => e.Value == "DepthRim_OceanMap"), "boarding raids target ocean maps");
        string patch = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Patches", "Boarding_Storyteller.xml"));
        Check(patch.Contains("DepthRim.StorytellerCompProperties_BoardingRaids") && patch.Contains("<incident>DepthRim_BoardingRaid</incident>"), "every storyteller gets the boarding raid comp");
        List<string> retreatGivers = Def("DutyDef", "DepthRim_ReturnToCraft").Element("thinkNode").Element("subNodes").Elements("li").Select(e => (string)e.Attribute("Class")).ToList();
        Check(retreatGivers.IndexOf("DepthRim.JobGiver_CarryToCraft") == 0 && retreatGivers.IndexOf("DepthRim.JobGiver_BoardCraft") == 1, "retreating boarders grab captives or loot, then board");
        Check((string)Def("JobDef", "DepthRim_CarryToCraft").Element("driverClass") == "DepthRim.JobDriver_CarryToCraft", "the carry-to-craft job exists");
        // 6.7 玩家的突擊艇：月池（非 edifice）、只能造在月池上、世界航跡
        XElement moonPool = Def("ThingDef", "DepthRim_MoonPool");
        Check((string)moonPool.Element("building").Element("isEdifice") == "false", "the moon pool is a floor building the craft can sit on");
        Check(TextureExists((string)moonPool.Element("graphicData").Element("texPath")), "the moon pool texture exists");
        XElement playerCraft = Def("ThingDef", "DepthRim_PlayerBoardingCraft");
        Check((string)playerCraft.Element("thingClass") == "DepthRim.Building_BoardingCraft", "the player craft shares the boarding craft class");
        Check(playerCraft.Element("placeWorkers").Elements("li").Any(e => e.Value == "DepthRim.PlaceWorker_OnMoonPool"), "the player craft must be built on a moon pool");
        Check((string)playerCraft.Element("size") == (string)craftDef.Element("size"), "the player craft has the same footprint as the skyfallers");
        foreach (string gated in new[] { "DepthRim_MoonPool", "DepthRim_PlayerBoardingCraft" })
        {
            Check(Def("ThingDef", gated).Element("researchPrerequisites").Elements("li").Any(e => e.Value == "DepthRim_UnderwaterCombat"), $"{gated} is unlocked by underwater combat");
        }
        Check((string)Def("WorldObjectDef", "DepthRim_TravelingBoardingCraft").Element("worldObjectClass") == "DepthRim.WorldObject_TravelingBoardingCraft", "the traveling craft world object exists");
        Check(File.ReadAllText(Path.Combine(zhRoot, "RaidStrategyDef", "DepthRim_Boarding.xml")).Contains("<DepthRim_Boarding.letterLabelEnemy>"), "the boarding raid letter is translated");
        Check(File.ReadAllText(Path.Combine(zhRoot, "PawnsArrivalModeDef", "DepthRim_Boarding.xml")).Contains("<DepthRim_BoardingArrival.textEnemy>"), "the boarding arrival text is translated");

        // 極地冰蓋有深淵坑當船塢
        XElement polar = Def("BiomeDef", "DepthRim_PolarIceCap").Element("modExtensions").Elements("li").Single(e => (string)e.Attribute("Class") == "DepthRim.WaterMapExtension");
        Check(polar.Element("abyssPockets") != null, "the polar ice cap carves abyss pockets for a shipyard");

        // 翻譯
        string zh = Path.Combine(ModRoot, "1.7", "Languages", "ChineseTraditional (繁體中文)");
        string things = File.ReadAllText(Path.Combine(zh, "DefInjected", "ThingDef", "DepthRim_Things.xml"));
        foreach (string name in new[] { "DepthRim_Keel", "DepthRim_KeelSmall", "DepthRim_Helm", "DepthRim_Thruster", "DepthRim_AdvancedThruster", "DepthRim_ActiveSonar", "DepthRim_PassiveSonar", "DepthRim_FireControlSonar", "DepthRim_Torpedo", "DepthRim_TorpedoTube", "DepthRim_AntiTorpedo", "DepthRim_SupercavitatingGun", "DepthRim_BoardingCraft", "DepthRim_MoonPool", "DepthRim_PlayerBoardingCraft" })
        {
            Check(things.Contains("<" + name + ".label>") && things.Contains("<" + name + ".description>"), $"{name} is translated");
        }
    }

    // 牆體水壓（設計稿 §5.1）：雙標籤 BFS 的拓樸與負載
    static void WallPressure()
    {
        // 地圖字元：# 障壁、A/B/C 房間、. 不屬於任何房間（深淵）
        List<WallPressureTopology.Entry> Topo(string[] rows, out int width)
        {
            int w = rows[0].Length;
            int h = rows.Length;
            width = w;
            char At(int i) => rows[h - 1 - i / w][i % w];
            return WallPressureTopology.Build(w, h, i => At(i) == '#', i => At(i) >= 'A' && At(i) <= 'Z' ? At(i) : -1);
        }
        WallPressureTopology.Entry? Find(List<WallPressureTopology.Entry> list, int width, int x, int rowFromTop, int height)
        {
            int idx = (height - 1 - rowFromTop) * width + x;
            foreach (var e in list) { if (e.cell == idx) return e; }
            return null;
        }

        // 單層牆：A | # | B
        var single = Topo(new[] { "A#B" }, out int w1);
        Check(Find(single, w1, 1, 0, 1)?.thickness == 1, "a single wall between two rooms is 1 thick");

        // 兩格厚的實心牆
        var thick = Topo(new[] { "A##B" }, out int w2);
        Check(Find(thick, w2, 1, 0, 1)?.thickness == 2 && Find(thick, w2, 2, 0, 1)?.thickness == 2, "a two-layer wall is 2 thick on both cells");

        // 雙層牆中間有夾層：A | # | C | # | B → 兩面牆各自是 1 格厚，分別隔 A/C 與 C/B
        var gap = Topo(new[] { "A#C#B" }, out int w3);
        Check(Find(gap, w3, 1, 0, 1)?.thickness == 1 && Find(gap, w3, 3, 0, 1)?.thickness == 1, "a double wall with a gap is two separate 1-thick walls");

        // 只面對一個房間（另一側是深淵或靠岩壁超過上限）：不受力
        var lonely = Topo(new[] { "A#." }, out int w4);
        Check(Find(lonely, w4, 1, 0, 1) == null, "a wall facing only one room carries nothing");
        var rock = Topo(new[] { "A#####B" }, out int w5);
        Check(rock.Count == 0, "rock thicker than the limit carries nothing");

        // 同一個房間的兩側（房間裡的隔板）：不受力
        var inside = Topo(new[] { "AAA", "A#A", "AAA" }, out int w6);
        Check(inside.Count == 0, "a pillar inside one room carries nothing");

        // 負載與損傷
        Near(WallPressureTopology.Load(1f, 0f, DepthTier.Deep, 1), 4f, "sea against a dry room in the deep: load 4");
        Near(WallPressureTopology.Load(1f, 0f, DepthTier.Deep, 2), 2f, "two layers halve the load");
        Near(WallPressureTopology.Load(1f, 0f, DepthTier.Deep, 4), 2f, "more than two layers add nothing");
        Near(WallPressureTopology.Load(1f, 1f, DepthTier.Seabed, 1), 0f, "no difference, no load");
        Near(WallPressureTopology.Load(0.25f, 1f, DepthTier.Thermocline, 1), 1.5f, "a part-flooded room carries less");
        Check(WallPressureTopology.DamagePerUpdate(1f, WallPressureTopology.Capacity(DepthTier.Shallow)) == 0f, "a plain wall holds in the shallows");
        Check(WallPressureTopology.DamagePerUpdate(4f, WallPressureTopology.Capacity(DepthTier.Shallow)) > 0f, "a plain wall is overloaded in the deep");
        Check(WallPressureTopology.DamagePerUpdate(8f, WallPressureTopology.Capacity(DepthTier.Seabed)) == 0f, "a seabed-rated wall holds at the seabed");
    }
}
