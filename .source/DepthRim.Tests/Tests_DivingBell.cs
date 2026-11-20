using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 潛水鐘與吊架（海岸造船 M5）：收放時間、鐘裡的殘氣、Def。
static partial class Test
{
    static void DivingBellMath()
    {
        Check(BellMath.TransitTicks(DepthTier.Shallow) == 20 * 60, $"the shallows take 20 seconds ({BellMath.TransitTicks(DepthTier.Shallow)} ticks)");
        Check(BellMath.TransitTicks(DepthTier.Seabed) == 60 * 60, "the deepest tier takes 60 seconds, the maximum");
        Check(BellMath.TransitTicks(DepthTier.Thermocline) > BellMath.TransitTicks(DepthTier.Shallow)
              && BellMath.TransitTicks(DepthTier.Deep) > BellMath.TransitTicks(DepthTier.Thermocline), "deeper takes longer");

        float reserve = 0f;
        Check(!BellMath.StepReserve(ref reserve, 250, 2, supplied: true) && reserve == BellMath.ReserveOccupantTicks, "with power the reserve is topped up");
        Check(!BellMath.StepReserve(ref reserve, 250, 2, supplied: false) && reserve == BellMath.ReserveOccupantTicks - 500f, "without power two people use it twice as fast");
        reserve = 100f;
        Check(!BellMath.StepReserve(ref reserve, 250, 1, supplied: false) && reserve == 0f, "the last of the reserve still counts as air this update");
        Check(BellMath.StepReserve(ref reserve, 250, 1, supplied: false), "then the people inside are out of air");
        reserve = 300f;
        Check(!BellMath.StepReserve(ref reserve, 250, 0, supplied: false) && reserve == 300f, "an empty bell does not use air");
        Check(BellMath.ReserveOccupantTicks / 2500f >= 1.99f, "one person lasts about two in-game hours on the reserve");
        Check(BellMath.SuffocationPerUpdate > 0f && BellMath.RecoveryPerUpdate > 0f, "suffocation and recovery rates are positive");
    }

    static void ContentDivingBell()
    {
        List<XElement> all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        XElement D(string name) => all.Single(e => e.Name.LocalName == "ThingDef" && (string)e.Element("defName") == name);
        XElement davit = D("DepthRim_BellDavit");
        XElement bell = D("DepthRim_DivingBell");
        Check((string)davit.Element("thingClass") == "DepthRim.Building_BellDavit" && (string)bell.Element("thingClass") == "DepthRim.Building_DivingBell", "davit and bell use their building classes");
        Check((string)davit.Element("size") == "(1,1)" && (string)bell.Element("size") == "(2,2)", "the davit is 1x1 and the bell 2x2");
        Check(davit.Element("researchPrerequisites").Elements("li").Any(e => e.Value == "DepthRim_CoastalShipbuilding")
              && bell.Element("researchPrerequisites").Elements("li").Any(e => e.Value == "DepthRim_CoastalShipbuilding"), "both come with coastal shipbuilding");
        Check(davit.Element("placeWorkers").Elements("li").Any(e => e.Value == "DepthRim.PlaceWorker_BellDavit"), "the davit can only go on a deck or moon pool");
        Check(davit.Element("comps").Elements("li").Any(e => (string)e.Attribute("Class") == "CompProperties_Power"), "the davit needs power");
        List<string> bellComps = bell.Element("comps").Elements("li").Select(e => (string)e.Attribute("Class")).ToList();
        Check(bellComps.Contains("DepthRim.CompProperties_UnderwaterTransporter") && bellComps.Contains("DepthRim.CompProperties_BellShuttle"), "the bell carries people in a transporter container with the bell's boarding rules");
        Check(TextureExists((string)davit.Element("graphicData").Element("texPath")) && TextureExists((string)bell.Element("graphicData").Element("texPath")), "placeholder textures exist");
        Check(Building_DivingBell.MaxPeople == 2, "two people fit in the bell");
    }
}
