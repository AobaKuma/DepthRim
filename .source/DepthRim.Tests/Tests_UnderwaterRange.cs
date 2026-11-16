using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 水中射程（2026-11-16）：射手泡在水裡時遠程射程 -75%，排乾的艙室不受影響；水下步槍不受限但射程比突擊步槍短。
static partial class Test
{
    static void UnderwaterRange()
    {
        Check(FloodingMath.RangeInWater(30.9f, shooterSubmerged: true, underwaterWeapon: false) == 30.9f * 0.25f, "a submerged shooter keeps 25% of a normal gun's range");
        Check(FloodingMath.RangeInWater(30.9f, shooterSubmerged: false, underwaterWeapon: false) == 30.9f, "a shooter in a drained compartment (not submerged) keeps full range");
        Check(FloodingMath.RangeInWater(21.9f, shooterSubmerged: true, underwaterWeapon: true) == 21.9f, "an underwater weapon keeps full range in water");
        // 淹水門檻和溺水共用：水淹到腰還能正常射擊
        Check(FloodingMath.SubmergedLevelStanding == FloodingPawnEffects.SubmergedLevelStanding, "range uses the same submerged line as drowning");

        XElement rifle = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "Weapons", "Weapons_Underwater.xml")).Root.Elements("ThingDef")
            .Single(e => (string)e.Element("defName") == "DepthRim_Gun_UnderwaterRifle");
        float range = float.Parse(rifle.Element("verbs").Element("li").Element("range").Value);
        Check(range < 30.9f, $"the underwater rifle reaches less far than an assault rifle on land ({range})");
        Check(range > 30.9f * FloodingMath.UnderwaterRangeFactor, $"underwater it outranges an assault rifle ({range})");
        Check(rifle.Element("modExtensions").Elements("li").Any(e => (string)e.Attribute("Class") == "DepthRim.UnderwaterWeaponExtension"), "the underwater rifle is marked as an underwater weapon");
        Check(File.Exists(Path.Combine(ModRoot, "Textures", "Thing", "Weapons", "DR_Rifle.png")) && (string)rifle.Element("graphicData").Element("texPath") == "Thing/Weapons/DR_Rifle", "the underwater rifle uses DR_Rifle.png");
    }
}
