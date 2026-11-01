using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 「冰洋」改成「海洋」（多生態區）之後的舊存檔相容：舊的 Def 名稱與類別名稱對應到新的。
    /// 舊世界的海洋格子全部是舊的冰洋生態區，讀檔後都會變成極地冰蓋；要有其他生態區得開新世界。
    /// 原版的轉換清單是 private，用反射加進去（只在遊戲啟動時一次）。
    /// </summary>
    public class BackCompatibilityConverter_Ocean : BackCompatibilityConverter
    {
        private static readonly Dictionary<string, string> PlanetLayerDefs = new Dictionary<string, string>
        {
            { "DepthRim_IceOcean", "DepthRim_Ocean" }
        };

        private static readonly Dictionary<string, string> OtherDefs = new Dictionary<string, string>
        {
            { "DepthRim_IceOcean_Settings", "DepthRim_Ocean_Settings" },
            { "DepthRim_IceOceanSite", "DepthRim_OceanSite" },
            { "DepthRim_IceOceanTiles", "DepthRim_OceanTiles" },
            { "DepthRim_IceOceanFog", "DepthRim_OceanFog" }
        };

        public override bool AppliesToVersion(int majorVer, int minorVer)
        {
            return true;
        }

        public override string BackCompatibleDefName(Type defType, string defName, bool forDefInjections = false, XmlNode node = null)
        {
            if (defName == null)
            {
                return null;
            }
            if (defName == "DepthRim_IceOcean")
            {
                // 同一個舊名字當過三種 Def：圖層、生態區、地圖生成器
                if (defType == typeof(RimWorld.BiomeDef))
                {
                    return "DepthRim_PolarIceCap";
                }
                if (defType == typeof(RimWorld.PlanetLayerDef) || defType == typeof(MapGeneratorDef))
                {
                    return PlanetLayerDefs[defName];
                }
            }
            return OtherDefs.TryGetValue(defName, out string renamed) ? renamed : null;
        }

        public override Type GetBackCompatibleType(Type baseType, string providedClassName, XmlNode node)
        {
            if (providedClassName == "DepthRim.WorldObject_IceOceanSite")
            {
                return typeof(WorldObject_OceanSite);
            }
            return null;
        }

        public override void PostExposeData(object obj)
        {
        }
    }

    [StaticConstructorOnStartup]
    public static class BackCompatibility_OceanRegistration
    {
        static BackCompatibility_OceanRegistration()
        {
            List<BackCompatibilityConverter> chain = Traverse.Create(typeof(BackCompatibility)).Field("conversionChain").GetValue<List<BackCompatibilityConverter>>();
            if (chain == null)
            {
                Log.Warning("[DepthRim] BackCompatibility.conversionChain not found; saves from before the ocean layer may not load.");
                return;
            }
            chain.Add(new BackCompatibilityConverter_Ocean());
        }
    }
}
