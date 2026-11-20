using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using DepthRim;
using RimWorld;
using Verse;

// 洋流發電機（2026-11-19）：比照原版風力發電機的輸出算法（額定 × 流強 × 通道折減，250 ticks 重算），
// 流強看深度層級／天氣／潮汐漲落；整台機器要浸在水裡。水下不能使用風力發電（補丁）。
static partial class Test
{
    static void OceanCurrentTurbinePower()
    {
        // ── 洋流強度 ──
        Near(OceanCurrentMath.TierBase(DepthTier.Shallow), 1f, "the shallows have the full current");
        Check(OceanCurrentMath.TierBase(DepthTier.Shallow) > OceanCurrentMath.TierBase(DepthTier.Thermocline)
              && OceanCurrentMath.TierBase(DepthTier.Thermocline) > OceanCurrentMath.TierBase(DepthTier.Deep)
              && OceanCurrentMath.TierBase(DepthTier.Deep) > OceanCurrentMath.TierBase(DepthTier.Seabed),
            "the current gets weaker with every depth tier");
        Near(OceanCurrentMath.TierBase(DepthTier.Seabed), 0.55f, "the abyssal seabed still has a usable current");
        Near(OceanCurrentMath.WeatherFactor(0.5f), 1f, "calm weather is 1x");
        Near(OceanCurrentMath.WeatherFactor(2f), OceanCurrentMath.MaxWeatherFactor, "a storm is capped at the max weather factor");
        Near(OceanCurrentMath.WeatherFactor(1.25f), 1.175f, "weather scales linearly between calm and storm");
        Check(OceanCurrentMath.WeatherShare(DepthTier.Shallow) > OceanCurrentMath.WeatherShare(DepthTier.Seabed),
            "storms reach the shallows far more than the seabed");
        Near(OceanCurrentMath.Surge(0, 0f), 1f, "the tide starts at its midpoint");
        Near(OceanCurrentMath.Surge(OceanCurrentMath.SurgePeriod / 4, 0f), 1f + OceanCurrentMath.SurgeAmplitude, "a quarter period later the tide peaks");
        Near(OceanCurrentMath.Surge(OceanCurrentMath.SurgePeriod * 3 / 4, 0f), 1f - OceanCurrentMath.SurgeAmplitude, "three quarters later it is at its lowest");
        Check(OceanCurrentMath.Phase(1234) >= 0f && OceanCurrentMath.Phase(1234) < 1f, "the phase is within one period");
        Check(Math.Abs(OceanCurrentMath.Phase(1) - OceanCurrentMath.Phase(2)) > 0.001f, "neighbouring turbines do not surge in step");
        // 淺海滿流：1（基準）× 1（平靜）× 1（潮位中點）
        Near(OceanCurrentMath.Strength(DepthTier.Shallow, 0.5f, 0, 0f), 1f, "a calm shallow sea runs at 1x");
        Near(OceanCurrentMath.Strength(DepthTier.Seabed, 0.5f, 0, 0f), 0.55f, "the seabed runs at 0.55x when calm");
        Check(OceanCurrentMath.Strength(DepthTier.Shallow, 2f, OceanCurrentMath.SurgePeriod / 4, 0f) <= OceanCurrentMath.MaxStrength,
            "the current never exceeds the strength cap");
        Check(OceanCurrentMath.Strength(DepthTier.Shallow, 2f, OceanCurrentMath.SurgePeriod / 4, 0f) > OceanCurrentMath.Strength(DepthTier.Deep, 2f, OceanCurrentMath.SurgePeriod / 4, 0f),
            "a storm surge is stronger in the shallows");

        // ── 通道阻塞與輸出 ──
        Near(OceanCurrentMath.FlowFactor(0), 1f, "an open current path costs nothing");
        Near(OceanCurrentMath.FlowFactor(1), 0.8f, "each obstructed cell costs 20% (vanilla wind parity)");
        Near(OceanCurrentMath.FlowFactor(3), 0.4f, "three obstructed cells cost 60%");
        Near(OceanCurrentMath.FlowFactor(20), OceanCurrentMath.MinFlowFactor, "a fully blocked path still leaves the minimum");
        Near(OceanCurrentMath.Output(-2500f, 1f, 0), 2500f, "a calm shallow turbine makes its rated power");
        Near(OceanCurrentMath.Output(-2500f, 1f, 1), 2000f, "one obstructed cell costs 20% of the output");
        Near(OceanCurrentMath.Output(-2500f, OceanCurrentMath.MaxStrength, 0), 3500f, "at the cap a 2500 W turbine makes 3500 W");
        Near(OceanCurrentMath.Output(300f, 1f, 0), 0f, "a consumer never generates");

        // ── 水流通道幾何 ──
        IntVec3 center = new IntVec3(10, 0, 10);
        IntVec2 size = new IntVec2(3, 3);
        List<IntVec3> north = OceanCurrentMath.FlowCells(center, Rot4.North, size, 2).ToList();
        Check(north.Count == 12, $"two cells of current path on each side of a 3x3 footprint ({north.Count} cells)");
        CellRect body = GenAdj.OccupiedRect(center, Rot4.North, size);
        Check(north.All(c => !body.Contains(c)), "the flow path never includes the turbine itself");
        Check(north.All(c => c.x >= 9 && c.x <= 11), "the flow path is exactly as wide as the turbine");
        Check(north.All(c => c.z < 9 || c.z > 11), "the flow path is in front of and behind the turbine only");
        List<IntVec3> east = OceanCurrentMath.FlowCells(center, Rot4.East, size, 2).ToList();
        Check(east.Count == 12 && east.All(c => c.z >= 9 && c.z <= 11) && east.All(c => c.x < 9 || c.x > 11),
            "an east-facing turbine draws its current along the x axis");
        Check(!OceanCurrentMath.FlowCells(center, Rot4.North, size, 0).Any(), "reach 0 means no flow path");
        Check(north.Count == east.Count, "the two rotations cover the same number of cells");

        // ── Def ──
        List<XElement> all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        XElement Def(string name) => all.FirstOrDefault(d => (string)d.Element("defName") == name);
        XElement def = Def("DR_OceanCurrentTurbine");
        if (!Check(def != null, "DR_OceanCurrentTurbine exists")) { return; }
        Check((string)def.Element("size") == "(3,3)", "the ocean current turbine is 3x3");
        Check((string)def.Element("rotatable") == "true", "it is rotatable so the current path can be aimed");
        Check((string)def.Element("blockWind") == "true", "turbines obstruct each other's current");
        Check((string)def.Element("terrainAffordanceNeeded") == "Light", "it can be anchored to the seabed");
        Check((string)def.Element("drawerType") == "MapMeshAndRealTime", "it is drawn every frame so the power bar follows the current");
        XElement graphicData = def.Element("graphicData");
        Check((string)graphicData?.Element("texPath") == "Thing/DR_CurrentTurbine/CurrentTurbine", "it uses its own four-way texture");
        Check((string)graphicData?.Element("graphicClass") == "Graphic_Multi", "the texture has a facing for every rotation");
        float shadowHeight = (float?)def.Element("staticSunShadowHeight") ?? 0f;
        Check(graphicData?.Element("shadowData") == null || shadowHeight <= 0f,
            "shadowData and staticSunShadowHeight are not combined (vanilla logs a config error for that)");
        Check((string)def.Element("designationCategory") == "DepthRim_Underwater", "it is built from the underwater tab");
        Check(def.Element("researchPrerequisites")?.Elements("li").Any(li => li.Value == "DepthRim_UnderwaterSurvival") == true,
            "it is unlocked by the starting underwater tech");
        Check(def.Element("placeWorkers")?.Elements("li").Any(li => li.Value == "DepthRim.PlaceWorker_OceanCurrentTurbine") == true,
            "placement requires being under water");
        XElement comp = def.Element("comps")?.Elements("li").FirstOrDefault(li => (string)li.Attribute("Class") == "DepthRim.CompProperties_OceanCurrentTurbine");
        if (!Check(comp != null, "the turbine has its own power comp")) { return; }
        float? rated = (float?)comp.Element("basePowerConsumption");
        Check(rated.HasValue && rated.Value < 0f, "it generates power instead of using it");
        Near(-rated.GetValueOrDefault(), 2500f, "rated output is 2500 W");
        Check((string)comp.Element("transmitsPower") == "true", "it feeds the power grid");
        Check((string)comp.Element("soundAmbientProducingPower") == "WaterMill_Ambience", "it sounds like moving water while producing");
        Check(def.Element("comps").Elements("li").Any(li => (string)li.Attribute("Class") == "CompProperties_Breakdownable"),
            "it can break down like other generators");

        CompProperties_OceanCurrentTurbine props = new CompProperties_OceanCurrentTurbine();
        Check(props.compClass == typeof(CompOceanCurrentTurbine), "the props create the turbine comp");
        Check(typeof(CompProperties_Power).IsAssignableFrom(props.GetType()), "the props derive from CompProperties_Power so the comp can read its rated power");
        Near(props.minWaterLevel, 0.5f, "half water is the minimum");
        Check(props.flowReach == OceanCurrentMath.DefaultFlowReach, "the default flow reach matches the math");
        Check(typeof(CompOceanCurrentTurbine).IsSubclassOf(typeof(CompPowerPlant)), "the comp is a power plant");
        Check(typeof(CompOceanCurrentTurbine).GetMethod("Recalculate") != null, "the comp recalculates the current");
        Check(CompOceanCurrentTurbine.UpdateInterval == 250, "it recalculates on the vanilla wind turbine interval");
        Check(!CompOceanCurrentTurbine.IsFullySubmerged(null, null, default(CellRect), 0.5f), "no map means no submergence");

        // ── 水下不能使用風力發電 ──
        string patchFile = System.IO.Path.Combine(ModRoot, "1.7", "Patches", "Power_WindTurbineUnderwater.xml");
        if (!Check(System.IO.File.Exists(patchFile), "the wind turbine underwater patch exists")) { return; }
        XDocument patch = XDocument.Load(patchFile);
        XElement op = patch.Root.Elements("Operation").FirstOrDefault(o => (string)o.Attribute("Class") == "PatchOperationAdd");
        if (!Check(op != null, "the patch adds a place worker to the wind turbine")) { return; }
        Check(((string)op.Element("xpath"))?.Contains("ThingDef[defName=\"WindTurbine\"]/placeWorkers") == true, "the patch targets the vanilla wind turbine's place workers");
        Check(op.Element("value")?.Elements("li").Any(li => li.Value == "DepthRim.PlaceWorker_NoWindUnderwater") == true,
            "the wind turbine gets the no-wind-underwater place worker");
        // 四個補丁目標都在原版組件裡（HarmonyTargets 另外直接驗證方法的參數）
        Assembly game = typeof(CompPowerPlantWind).Assembly;
        Check(game == typeof(Verse.Thing).Assembly, "the wind turbine comp lives in the real game assembly");
        Check(typeof(CompPowerPlantWind).GetProperty("DesiredPowerOutput", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) != null,
            "the wind turbine output getter still exists");
        Check(typeof(PlaceWorker).IsAssignableFrom(typeof(PlaceWorker_NoWindUnderwater)), "the place worker derives from Verse.PlaceWorker");

        // 翻譯：新按鍵兩種語言都有（ContentKeyed 已全面檢查，這裡確認最關鍵的幾條）
        foreach (string language in new[] { "English", "ChineseTraditional (繁體中文)" })
        {
            string keyed = System.IO.Path.Combine(ModRoot, "1.7", "Languages", language, "Keyed", "DepthRim.xml");
            string text = System.IO.File.ReadAllText(keyed);
            foreach (string key in new[] { "DepthRim.CurrentTurbine.NotSubmerged", "DepthRim.WindTurbine.NoWindUnderwater" })
            {
                Check(text.Contains("<" + key + ">"), $"{language} has {key}");
            }
        }
    }
}
