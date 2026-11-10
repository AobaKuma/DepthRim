using DMSE;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 聲納是 DMSE 搜索雷達（<c>CompSearchRadar</c>）的換皮：任何有運作中搜索雷達的地圖（包括航行中的潛艇）
    /// 都是 BVR 節點，偵測、預警與世界地圖上的偵測圈全部沿用 DMSE。
    /// </summary>
    public static class SonarUtility
    {
        /// <summary>地圖上有沒有某陣營運作中的主動聲納（會暴露位置的搜索雷達）。之後的水下襲擊以此為條件（設計稿 §6）。</summary>
        public static bool HasActiveSonar(Map map, Faction faction)
        {
            MapComponent_BVRCombat comp = map?.GetComponent<MapComponent_BVRCombat>();
            if (comp == null)
            {
                return false;
            }
            foreach (CompSearchRadar radar in comp.searchRadars)
            {
                if (radar.Active && radar.parent.Faction == faction && !radar.Props.immuneToAntiRadiationSeeker)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 目標的深度：目標世界物件自己提供層級（潛艇、之後的魚雷）時才有；沒有就不套溫躍層遮蔽。
        /// </summary>
        public static bool TryTargetTier(object track, out DepthTier tier)
        {
            if (track is IDepthTierProvider provider)
            {
                tier = provider.CurrentDepthTier;
                return true;
            }
            tier = DepthTier.Shallow;
            return false;
        }
    }

    /// <summary>溫躍層遮蔽：海洋層上的節點偵測有深度的目標時，依兩者的層級打折（<see cref="SonarMath.LayerFactor"/>）。</summary>
    [HarmonyPatch(typeof(BVRNode), nameof(BVRNode.DetectionRange))]
    public static class Patch_SonarLayer
    {
        [HarmonyPostfix]
        public static void Postfix(BVRNode __instance, IBVRTrack track, ref float __result)
        {
            if (__result <= 0f || track == null || __instance?.parent == null || !__instance.parent.HasMap)
            {
                return;
            }
            Map map = __instance.parent.Map;
            if (!OceanRouting.IsOcean(map.Tile) || !SonarUtility.TryTargetTier(track, out DepthTier target))
            {
                return;
            }
            __result *= SonarMath.LayerFactor(FloodingUtility.DepthTierOf(map), target);
        }
    }
}
