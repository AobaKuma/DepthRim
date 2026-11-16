using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 壓力適應（PressureAdaptationMath）與加壓艙的內容檢查。
static partial class Test
{
    static void PressureAdaptation()
    {
        const int day = PressureAdaptationMath.TicksPerDay;

        // 適應 1 級的時間：0.5～2 天，由血液過濾能力決定（一般人 1 天）
        Near(PressureAdaptationMath.DaysPerLevel(1f), 1f, "normal blood filtration: 1 day per level");
        Near(PressureAdaptationMath.DaysPerLevel(2f), 0.5f, "200% filtration: half a day per level");
        Near(PressureAdaptationMath.DaysPerLevel(4f), 0.5f, "never faster than half a day");
        Near(PressureAdaptationMath.DaysPerLevel(0.5f), 2f, "50% filtration: 2 days per level");
        Near(PressureAdaptationMath.DaysPerLevel(0.2f), 2f, "never slower than 2 days");
        Near(PressureAdaptationMath.DaysPerLevel(0f), 2f, "no blood filtration at all still adapts, at the slowest rate");
        for (float b = 0.1f; b <= 2.5f; b += 0.1f)
        {
            float d = PressureAdaptationMath.DaysPerLevel(b);
            Check(d >= 0.5f && d <= 2f, $"filtration {b:F1}: {d:F2} days is within 0.5-2");
        }

        // 差不超過 1 級沒有不適；超過的部分是嚴重度；兩個方向都算
        Near(PressureAdaptationMath.Mismatch(0f, 0f), 0f, "same level: fine");
        Near(PressureAdaptationMath.Mismatch(0f, 1f), 0f, "one level deeper: fine");
        Near(PressureAdaptationMath.Mismatch(1f, 0f), 0f, "one level shallower: fine");
        Near(PressureAdaptationMath.Mismatch(0f, 2f), 1f, "two levels deeper: mismatch 1");
        Near(PressureAdaptationMath.Mismatch(0f, 3f), 2f, "surface to seabed: mismatch 2");
        Near(PressureAdaptationMath.Mismatch(3f, 0f), 2f, "seabed to surface: mismatch 2");
        Near(PressureAdaptationMath.Mismatch(1.5f, 3f), 0.5f, "half adapted");

        // 往環境移動：一級剛好花 DaysPerLevel 天，不會超過
        Near(PressureAdaptationMath.Step(0f, 2f, 1f, day), 1f, "one level after 1 day at normal filtration");
        Near(PressureAdaptationMath.Step(0f, 2f, 2f, day / 2), 1f, "one level after half a day at 200% filtration");
        Near(PressureAdaptationMath.Step(0f, 2f, 0.5f, 2 * day), 1f, "one level after 2 days at 50% filtration");
        Near(PressureAdaptationMath.Step(3f, 0f, 1f, day), 2f, "adapts back up at the same rate");
        Near(PressureAdaptationMath.Step(0.9f, 1f, 1f, day), 1f, "does not overshoot the surroundings");
        Near(PressureAdaptationMath.Step(1f, 1f, 1f, day), 1f, "stays put once adapted");
        Near(PressureAdaptationMath.Step(0.5f, 2f, 1f, 0), 0.5f, "no time, no change");

        // 小步累加和一次算完一樣（hediff 每 250 ticks 結算一次）
        float stepped = 0f;
        for (int t = 0; t < day; t += 250) { stepped = PressureAdaptationMath.Step(stepped, 3f, 1f, 250); }
        Near(stepped, 1f, "250-tick steps add up to one level in a day", 0.002f);

        // 還要多久才不會不適
        Check(PressureAdaptationMath.TicksUntilComfortable(0f, 1f, 1f) == 0, "within tolerance: no wait");
        Check(PressureAdaptationMath.TicksUntilComfortable(0f, 2f, 1f) == day, "two levels down: a day of discomfort");
        Check(PressureAdaptationMath.TicksUntilComfortable(0f, 3f, 1f) == 2 * day, "surface to seabed: 2 days");
        Check(PressureAdaptationMath.TicksUntilComfortable(3f, 0f, 2f) == day, "seabed to surface with good kidneys: 1 day");
        float after = PressureAdaptationMath.Step(0f, 2f, 1f, PressureAdaptationMath.TicksUntilComfortable(0f, 2f, 1f));
        Near(PressureAdaptationMath.Mismatch(after, 2f), 0f, "after that wait the mismatch is gone");

        // 分段減壓：從海床（3）上來，先進 2 級的加壓艙，再降到 1、0，全程沒有不適
        float adapted = 3f;
        bool comfortable = true;
        foreach (int chamber in new[] { 2, 1, 0 })
        {
            comfortable &= PressureAdaptationMath.Mismatch(adapted, chamber) == 0f;
            adapted = PressureAdaptationMath.Step(adapted, chamber, 1f, day);
        }
        Check(comfortable, "stepping the chamber down one level at a time never causes discomfort");
        Near(adapted, 0f, "three steps bring a seabed crew back to surface pressure");

        Near(PressureAdaptationMath.MoveTowards(0f, 2f, 0.1f), 0.1f, "chamber pressure moves towards the target");
        Near(PressureAdaptationMath.MoveTowards(1.95f, 2f, 0.1f), 2f, "chamber pressure stops at the target");
        Near(PressureAdaptationMath.MoveTowards(2f, 0f, 0.5f), 1.5f, "chamber pressure can go down");
        Check(PressureAdaptationMath.MaxLevel == (int)DepthTier.Seabed, "the top pressure level is the seabed tier");

        // 加壓艙門的洩壓聲（2026-11-17）：壓差夠大才響，同一扇門有冷卻
        Check(!PressureAdaptationMath.DoorHisses(0f, 1000, -1), "no hiss between rooms at the same pressure");
        Check(!PressureAdaptationMath.DoorHisses(0.4f, 1000, -1), "no hiss for a small pressure difference");
        Check(PressureAdaptationMath.DoorHisses(0.5f, 1000, -1), "a half-level difference hisses");
        Check(PressureAdaptationMath.DoorHisses(-2f, 1000, -1), "the direction of the difference does not matter");
        Check(!PressureAdaptationMath.DoorHisses(2f, 1000 + PressureAdaptationMath.DoorHissCooldownTicks - 1, 1000), "the same door stays quiet during its cooldown");
        Check(PressureAdaptationMath.DoorHisses(2f, 1000 + PressureAdaptationMath.DoorHissCooldownTicks, 1000), "the door hisses again once the cooldown is over");
        Check(PressureAdaptationMath.DoorHissMinDifference > PressureAdaptationMath.DoorOpeningLeak, "one door opening never drains a chamber below the hiss threshold in one go");

        // 水密門的壓差通行規則：過去會更不適就不放行；徵召不擋；已經不適的人可以往比較不糟的一側走
        Check(PressureAdaptationMath.DoorAllows(false, 0f, 0f, 1f), "one level up through the door: allowed");
        Check(!PressureAdaptationMath.DoorAllows(false, 0f, 0f, 2f), "an unadapted colonist will not walk into a level-2 chamber");
        Check(PressureAdaptationMath.DoorAllows(true, 0f, 0f, 2f), "a drafted colonist goes where ordered");
        Check(!PressureAdaptationMath.DoorAllows(false, 3f, 2f, 0f), "a seabed-adapted colonist stays in the level-2 chamber");
        Check(!PressureAdaptationMath.DoorAllows(false, 1.5f, 2f, 0f), "half-adapted: still held in the chamber");
        Check(PressureAdaptationMath.DoorAllows(false, 1f, 2f, 0f), "adapted down to 1: free to leave to the surface");
        Check(PressureAdaptationMath.DoorAllows(false, 0f, 2f, 0f), "someone suffering inside may leave to where they are fine");
        Check(PressureAdaptationMath.DoorAllows(false, 3f, 0f, 2f), "someone suffering outside may enter the chamber that suits them");
        Check(PressureAdaptationMath.DoorAllows(false, 3f, 0f, 1f), "moving to a less bad side is allowed even if still uncomfortable");
        Check(!PressureAdaptationMath.DoorAllows(false, 3f, 1f, 0f), "moving to a worse side is not");
        Check(PressureAdaptationMath.DoorAllows(false, 2f, 2f, 2f), "no pressure difference: always allowed");

        // 壓力等級和牆體水壓的絕對壓力：和外海同一套單位（常壓 0、淺海 1、變溫層 2、深海 4、海床 8）
        Near(PressureAdaptationMath.GaugeOfLevel(0f), 0f, "level 0 is surface pressure (no load on walls)");
        for (DepthTier t = DepthTier.Thermocline; t <= DepthTier.Seabed; t++)
        {
            Near(PressureAdaptationMath.GaugeOfLevel((int)t), FloodingMath.PressureFactor(t), $"a level-{(int)t} chamber presses like the {t} sea");
            Near(PressureAdaptationMath.LevelOfGauge(FloodingMath.PressureFactor(t)), (int)t, $"the {t} sea is pressure level {(int)t}");
        }
        Near(PressureAdaptationMath.LevelOfGauge(FloodingMath.PressureFactor(DepthTier.Shallow)), 0f, "the shallows are level 0 (no adaptation needed)");
        Near(PressureAdaptationMath.LevelOfGauge(0.5f), 0f, "a half-flooded compartment in the shallows is level 0");
        Near(PressureAdaptationMath.LevelOfGauge(PressureAdaptationMath.GaugeOfLevel(1.5f)), 1.5f, "the two conversions round-trip");
        Near(PressureAdaptationMath.LevelOfGauge(100f), PressureAdaptationMath.MaxLevel, "capped at the seabed level");

        // 牆的負載（絕對壓力差 ÷ 厚度）與耐壓上限：加壓艙和外海用同一套
        float cap(DepthTier r) => WallPressureTopology.Capacity(r);
        float load(float a, float b, int t = 1) => WallPressureTopology.LoadGauge(PressureAdaptationMath.GaugeOfLevel(a), PressureAdaptationMath.GaugeOfLevel(b), t);
        Near(WallPressureTopology.LoadGauge(0f, FloodingMath.PressureFactor(DepthTier.Deep), 1), WallPressureTopology.Load(0f, 1f, DepthTier.Deep, 1), "the gauge load equals the old sea load for a dry compartment");
        Near(WallPressureTopology.LoadGauge(0f, FloodingMath.PressureFactor(DepthTier.Deep), 2), WallPressureTopology.Load(0f, 1f, DepthTier.Deep, 2), "same with a double wall");
        Check(load(1f, 0f) > cap(DepthTier.Shallow), "a single ordinary wall cannot hold a level-1 chamber on the surface (same as against the thermocline sea)");
        Check(load(1f, 0f, 2) <= cap(DepthTier.Shallow), "a double ordinary wall can");
        Check(load(1f, 0f) <= cap(DepthTier.Thermocline), "a watertight door holds level 1 against the surface");
        Check(load(2f, 0f) > cap(DepthTier.Thermocline), "a watertight door cannot hold level 2 against the surface (needs an antechamber)");
        Check(load(2f, 1f) <= cap(DepthTier.Thermocline), "a watertight door between level 2 and level 1 holds");
        Check(load(3f, 2f) > cap(DepthTier.Thermocline), "no door holds level 3 against level 2 yet");
        Check(load(3f, 0f) <= cap(DepthTier.Seabed), "a pressure hull wall holds anything");
        Check(load(2f, 2f) == 0f, "no difference, no load");
        // 潛艇：乾艙是常壓，加壓到和外海一樣時艙壁不受力
        Near(WallPressureTopology.LoadGauge(PressureAdaptationMath.GaugeOfLevel(3f), FloodingMath.PressureFactor(DepthTier.Seabed), 1), 0f, "a chamber at sea pressure takes no load from the sea");
        Check(PressureAdaptationMath.UnheldLeakPerHour > 0f, "rooms that cannot hold pressure leak");
        Check(PressureAdaptationMath.OpenDoorLeakPerHour > new CompProperties_Pressurizer().levelsPerHour, "a door held open leaks faster than the pressuriser fills");
        Check(PressureAdaptationMath.DoorOpeningLeak > 0f && PressureAdaptationMath.DoorOpeningLeak <= 0.25f, "one door opening costs a little pressure");

        // 出艙的水壓抗性：水深層級 − 裝備 − 適應值
        Near(FloodingMath.PressureShortfall(DepthTier.Shallow, 0f, 0f), 0f, "no shortfall in the shallows");
        Near(FloodingMath.PressureShortfall(DepthTier.Thermocline, 1f, 0f), 0f, "diving suit alone reaches the thermocline (unchanged)");
        Near(FloodingMath.PressureShortfall(DepthTier.Deep, 1f, 0f), 1f, "diving suit alone is one short in the deep (unchanged)");
        Near(FloodingMath.PressureShortfall(DepthTier.Deep, 1f, 1f), 0f, "adapted to level 1 plus a suit: the deep is safe");
        Near(FloodingMath.PressureShortfall(DepthTier.Seabed, 1f, 2f), 0f, "adapted to level 2 plus a suit: the seabed is safe");
        Near(FloodingMath.PressureShortfall(DepthTier.Seabed, 0f, 3f), 0f, "a fully adapted body needs no suit for the pressure");
        Near(FloodingMath.PressureShortfall(DepthTier.Seabed, 1f, 0.5f), 1.5f, "partial adaptation counts partially");
        Near(FloodingMath.PressureShortfall(DepthTier.Thermocline, 3f, 3f), 0f, "never negative");
    }

    static void ContentPressure()
    {
        string defs = Path.Combine(ModRoot, "1.7", "Defs");
        XDocument doc = XDocument.Load(Path.Combine(defs, "Pressure", "Pressure.xml"));

        // 適應 hediff：第一階段沒有效果，之後三個階段不致命
        XElement hediff = doc.Root.Elements("HediffDef").Single(e => (string)e.Element("defName") == "DepthRim_PressureAdaptation");
        Check((string)hediff.Element("hediffClass") == "DepthRim.Hediff_PressureAdaptation", "adaptation hediff uses its own class");
        Check((string)hediff.Element("isBad") == "false", "not a bad hediff (healing items must not wipe the adapted level)");
        Check(hediff.Element("lethalSeverity") == null, "pressure discomfort is never lethal");
        Near(float.Parse((string)hediff.Element("minSeverity")), Hediff_PressureAdaptation.RestingSeverity, "def minSeverity matches the resting severity", 0.00001f);
        Check(float.Parse((string)hediff.Element("maxSeverity")) >= PressureAdaptationMath.MaxLevel - PressureAdaptationMath.Tolerance, "max severity covers the largest mismatch");
        var stages = hediff.Element("stages").Elements("li").ToList();
        Check(stages.Count == 4, "four stages: adapted plus three levels of discomfort");
        Check(stages[0].Elements().All(e => e.Name.LocalName == "minSeverity"), "the first stage has no effects");
        Check(float.Parse((string)stages[1].Element("minSeverity")) > Hediff_PressureAdaptation.RestingSeverity, "the resting severity sits below the first discomfort stage");
        Check(stages.Skip(1).All(s => s.Element("lifeThreatening") == null), "no stage is life threatening");
        Check(stages.Skip(1).All(s => s.Element("capMods") != null && s.Element("label") != null), "each discomfort stage has a label and penalties");

        // 耐壓甲板：一般地板，提供船體平台的承載力
        XElement deck = doc.Root.Elements("TerrainDef").Single(e => (string)e.Element("defName") == "DepthRim_PressureDeck");
        Check((string)deck.Attribute("ParentName") == "FloorBase", "pressure deck is an ordinary floor (buildable on solid ground, removable)");
        Check(deck.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_HullPlatform"), "pressure deck provides the hull platform affordance");
        Check(TextureExists((string)deck.Element("texturePath")), "pressure deck texture exists");
        Check((string)deck.Element("designationCategory") == "DepthRim_Underwater", "pressure deck is in the underwater tab");
        XDocument platformDoc = XDocument.Load(Path.Combine(defs, "Buildings", "HullPlatform.xml"));
        XElement platform = platformDoc.Root.Elements("TerrainDef").Single(e => (string)e.Element("defName") == "DepthRim_HullPlatform");
        Check(platform.Element("affordances").Elements("li").Any(e => e.Value == "DepthRim_HullPlatform"), "hull platform counts as chamber floor too");

        // 加壓機
        XElement machine = doc.Root.Elements("ThingDef").Single(e => (string)e.Element("defName") == "DepthRim_Pressurizer");
        Check((string)machine.Element("tickerType") == "Rare", "pressuriser ticks rarely (CompTickRare)");
        Check((string)machine.Element("terrainAffordanceNeeded") == "DepthRim_HullPlatform", "pressuriser stands on pressure deck or hull platform");
        Check((string)machine.Element("designationCategory") == "DepthRim_Underwater", "pressuriser is in the underwater tab");
        var comps = machine.Element("comps").Elements("li").Select(e => (string)e.Attribute("Class")).ToList();
        Check(comps.Contains("DepthRim.CompProperties_Pressurizer") && comps.Contains("CompProperties_Power"), "pressuriser has its comp and needs power");
        string research = (string)machine.Element("researchPrerequisites").Element("li");
        Check(research == (string)deck.Element("researchPrerequisites").Element("li"), "pressuriser and pressure deck unlock together");
        XDocument doors = XDocument.Load(Path.Combine(defs, "Flooding", "Flooding_Things.xml"));
        XElement door = doors.Root.Elements("ThingDef").Single(e => (string)e.Element("defName") == "DepthRim_WatertightDoor");
        Check((string)door.Element("terrainAffordanceNeeded") == "Heavy", "the watertight door can be built on ordinary ground and on pressure deck");

        CompProperties_Pressurizer props = new CompProperties_Pressurizer();
        Check(props.compClass == typeof(CompPressurizer), "props point at the comp class");
        Check(props.levelsPerHour > PressureAdaptationMath.UnheldLeakPerHour, "a running pressuriser outpaces the leak of a room that cannot hold pressure");
    }
}
