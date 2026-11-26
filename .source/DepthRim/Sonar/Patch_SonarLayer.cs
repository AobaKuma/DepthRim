using DMSE;
using Fortified;
using RimWorld.Planet;
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
            MapComponent_AirDefense comp = map?.GetComponent<MapComponent_AirDefense>();
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

    /// <summary>
    /// 溫躍層遮蔽：海洋層上的節點偵測有深度的目標時，依兩者的層級打折（<see cref="SonarMath.LayerFactor"/>）。
    /// 走 DMSE 的全域修正鏈（<see cref="AirDefensePipeline"/>），所以涵蓋圈、首次偵測與攔截都一致；
    /// 觀測方是地圖就看地圖的層級，沒有地圖的節點（海盜潛艇的抽象防空）看它自己的巡航深度。
    /// 目標的深度：自己提供層級的世界物件（潛艇），或魚雷航跡帶的深度（<see cref="MissileBehavior_Torpedo"/>）。
    /// </summary>
    public class SonarLayerModifier : AirDefenseModifier
    {
        public static readonly SonarLayerModifier Instance = new SonarLayerModifier();

        public static bool TryObserverTier(AirDefenseQuery query, out DepthTier tier)
        {
            tier = DepthTier.Shallow;
            Map map = query.Map ?? (query.Site != null && query.Site.HasMap ? query.Site.Map : null);
            if (map != null)
            {
                if (!OceanRouting.IsOcean(map.Tile))
                {
                    return false;
                }
                tier = FloodingUtility.DepthTierOf(map);
                return true;
            }
            if (query.Site is IDepthTierProvider provider && OceanRouting.IsOcean(query.Site.Tile))
            {
                tier = provider.CurrentDepthTier;
                return true;
            }
            return false;
        }

        public static bool TryTargetTier(AirDefenseQuery query, out DepthTier tier)
        {
            WorldObject target = query.Track?.Self ?? query.TargetObject;
            MissileBehavior_Torpedo torpedo = TorpedoUtility.BehaviorOf(target);
            if (torpedo != null)
            {
                tier = torpedo.depth;
                return true;
            }
            return SonarUtility.TryTargetTier(target, out tier);
        }

        public override void Modify(AirDefenseQuery query, ref float value)
        {
            if (query.Stat != AirDefenseStat.Detection || query.Layer != AirCombatLayer.World || value <= 0f)
            {
                return;
            }
            if (!TryObserverTier(query, out DepthTier observer) || !TryTargetTier(query, out DepthTier target))
            {
                return;
            }
            value *= SonarMath.LayerFactor(observer, target);
        }
    }

    [StaticConstructorOnStartup]
    public static class SonarLayerRegistration
    {
        static SonarLayerRegistration()
        {
            AirDefensePipeline.Add(SonarLayerModifier.Instance);
        }
    }
}
