using System.Collections.Generic;
using DMSE;
using Fortified;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 魚雷：DMSE 飛彈換皮（設計稿 §11 M5）。彈體掛這個標記就是魚雷：只能走海路打海洋格（<see cref="Patch_TorpedoEngage"/>），
    /// 而且帶著發射時的深度（<see cref="MissileBehavior_Torpedo"/>），溫躍層遮蔽偵測。
    /// </summary>
    public class TorpedoExtension : DefModExtension
    {
    }

    /// <summary>反魚雷魚雷的彈體標記：只攔截魚雷，也只有它（和近防砲）能攔截魚雷（<see cref="Patch_TorpedoInterceptors"/>）。</summary>
    public class AntiTorpedoExtension : DefModExtension
    {
    }

    public static class TorpedoUtility
    {
        public static bool IsTorpedo(MissileConfig config)
        {
            return config?.body != null && config.body.HasModExtension<TorpedoExtension>();
        }

        public static bool IsAntiTorpedo(MissileConfig config)
        {
            return config?.body != null && config.body.HasModExtension<AntiTorpedoExtension>();
        }

        /// <summary>世界物件是不是一條魚雷航跡。</summary>
        public static bool IsTorpedoTrack(WorldObject o)
        {
            return o is MissileWorldObject missile && IsTorpedo(missile.config);
        }

        private static readonly AccessTools.FieldRef<MissileBehaviorSet, List<MissileBehavior>> BehaviorList =
            AccessTools.FieldRefAccess<MissileBehaviorSet, List<MissileBehavior>>("behaviors");

        /// <summary>航跡上的魚雷行為（記著深度）；不是魚雷回傳 null。</summary>
        public static MissileBehavior_Torpedo BehaviorOf(object track)
        {
            if (!(track is MissileWorldObject missile) || missile.Behaviors == null)
            {
                return null;
            }
            List<MissileBehavior> list = BehaviorList(missile.Behaviors);
            if (list == null)
            {
                return null;
            }
            foreach (MissileBehavior b in list)
            {
                if (b is MissileBehavior_Torpedo torpedo)
                {
                    return torpedo;
                }
            }
            return null;
        }

        /// <summary>
        /// 魚雷能不能從 <paramref name="from"/> 打 <paramref name="to"/>：兩端都要在海洋層、目標是海洋格、
        /// 海路到得了，而且海路距離不超過射程（0 = 不限）。回傳 null 表示可以，否則是翻譯鍵。
        /// </summary>
        public static string BlockReason(PlanetTile from, PlanetTile to, int range, out int seaDistance)
        {
            seaDistance = -1;
            if (!OceanRouting.IsOcean(from) || !OceanRouting.IsOcean(to) || !OceanRouting.Navigable(to))
            {
                return "DepthRim.Torpedo.SeaOnly";
            }
            seaDistance = OceanRouting.Distance(from, to);
            if (seaDistance >= OceanRouting.Unreachable)
            {
                return "DepthRim.Torpedo.NoSeaRoute";
            }
            if (range > 0 && seaDistance > range)
            {
                return "DepthRim.Torpedo.OutOfRange";
            }
            return null;
        }
    }

    /// <summary>魚雷只走海路：取代 DMSE 的直線射程判定。</summary>
    [HarmonyPatch(typeof(MissileWorldUtility), nameof(MissileWorldUtility.CanEngage))]
    public static class Patch_TorpedoEngage
    {
        /// <summary>魚雷整個由這裡判定（兩端都在海洋層，所以 DMSE 的跨圖層規則用不到）；其他飛彈照 DMSE。</summary>
        [HarmonyPrefix]
        public static bool Prefix(MissileConfig config, PlanetTile from, PlanetTile to, float maxRange, ref string reason, ref bool __result)
        {
            if (!TorpedoUtility.IsTorpedo(config) || !from.Valid || !to.Valid)
            {
                return true;
            }
            int range = config.Range;
            if (maxRange > 0f && (range <= 0 || maxRange < range))
            {
                range = (int)maxRange;
            }
            string key = TorpedoUtility.BlockReason(from, to, range, out _);
            __result = key == null;
            reason = key?.Translate(range);
            return false;
        }
    }

    /// <summary>魚雷在航跡上的狀態：發射時所在的深度（存檔）。</summary>
    public class MissileBehavior_Torpedo : MissileBehavior
    {
        public DepthTier depth = DepthTier.Shallow;

        public override void PostLaunch()
        {
            Map map = null;
            if (track is ScorerProjectile_WorldObject scorer && scorer.launcherThing != null)
            {
                map = scorer.launcherThing.MapHeld;
            }
            if (map == null && track is MissileWorldObject missile)
            {
                map = Find.WorldObjects.MapParentAt(missile.Origin)?.Map;
            }
            if (map != null)
            {
                depth = FloodingUtility.DepthTierOf(map);
                return;
            }
            // 沒有地圖的發射者（敵方魚雷艇）：用它的巡航深度，否則當作貼著那一格的海床。
            if (track is MissileWorldObject origin && origin.Origin.Valid)
            {
                WorldObject_EnemyTorpedoBoat boat = WorldObject_EnemyTorpedoBoat.At(origin.Origin);
                depth = boat != null ? boat.depth : DepthTierUtility.MaxTierAt(origin.Origin);
            }
        }

        /// <summary>
        /// 打沒有地圖的海上目標（§11 M7 7.2）：海盜潛艇扣船殼（歸零沉沒），敵方魚雷艇一發就沉。
        /// 回傳 true 表示已在這裡結算，DMSE 不再走一般的抵達流程。
        /// </summary>
        public override bool PreArrive()
        {
            if (!(track is MissileWorldObject missile))
            {
                return false;
            }
            PlanetTile dest = missile.Destination;
            MapParent parent = missile.TargetParent ?? Find.WorldObjects.MapParentAt(dest);
            if (parent is WorldObject_PirateSubmarine sub && !sub.Destroyed && !sub.HasMap && sub.Tile == dest)
            {
                sub.TakeTorpedoHit(missile.config?.DamageAmount ?? 100);
                return true;
            }
            WorldObject_EnemyTorpedoBoat boat = WorldObject_EnemyTorpedoBoat.At(dest);
            if (boat != null && boat.Faction != missile.Faction && Find.WorldObjects.MapParentAt(dest) == null)
            {
                Messages.Message("DepthRim.Pirate.BoatSunk".Translate(boat.Faction?.Name ?? ""), new GlobalTargetInfo(dest), MessageTypeDefOf.PositiveEvent);
                boat.Destroy();
                return true;
            }
            return false;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref depth, "depth", DepthTier.Shallow);
        }
    }

    /// <summary>
    /// 溫躍層遮蔽：海洋層上的節點偵測魚雷時，依觀測方地圖的層級與魚雷的深度打折（<see cref="SonarMath.LayerFactor"/>）。
    /// 經 DMSE 的修正鏈（目標裝配的 <c>ModifyDefenseAsTarget</c>），所以涵蓋圈、首次偵測與攔截都一致。
    /// </summary>
    public class TorpedoBehaviorProps : MissileBehaviorProps
    {
        public TorpedoBehaviorProps()
        {
            behaviorClass = typeof(MissileBehavior_Torpedo);
        }

        public override void ModifyDefenseAsTarget(AirDefenseQuery query, ref float value)
        {
            if (query == null || query.Stat != AirDefenseStat.Detection || query.Site == null || !query.Site.HasMap)
            {
                return;
            }
            Map map = query.Site.Map;
            if (!OceanRouting.IsOcean(map.Tile))
            {
                return;
            }
            MissileBehavior_Torpedo torpedo = TorpedoUtility.BehaviorOf(query.Track);
            if (torpedo == null)
            {
                return;
            }
            value *= SonarMath.LayerFactor(FloodingUtility.DepthTierOf(map), torpedo.depth);
        }
    }

    /// <summary>
    /// 攔截的分工（設計稿 §11 M5 5.7）：魚雷只會被海洋層上的防禦節點接戰，而且只有反魚雷魚雷與近防砲打得到；
    /// 反魚雷魚雷也不會拿去打空中的飛彈。
    /// </summary>
    [HarmonyPatch(typeof(BVRNode), nameof(BVRNode.PolicyAllows))]
    public static class Patch_TorpedoInterceptors
    {
        [HarmonyPostfix]
        public static void PolicyAllows(BVRNode __instance, IBVRTrack track, ref bool __result)
        {
            if (__result && track?.Self != null && TorpedoUtility.IsTorpedoTrack(track.Self)
                && (__instance.parent == null || !OceanRouting.IsOcean(__instance.Tile)))
            {
                __result = false;
            }
        }

    }

    /// <summary>反魚雷魚雷只打魚雷；其他攔截彈不打魚雷。</summary>
    [HarmonyPatch(typeof(CompMissileLauncher_Interceptor), nameof(CompMissileLauncher_Interceptor.CanEngage))]
    public static class Patch_TorpedoInterceptorLaunchers
    {
        [HarmonyPostfix]
        public static void LauncherCanEngage(CompMissileLauncher_Interceptor __instance, AirEngagement engagement, ref bool __result)
        {
            if (!__result || engagement == null)
            {
                return;
            }
            bool torpedo = TorpedoUtility.IsTorpedoTrack(engagement.TargetObject);
            bool antiTorpedo = TorpedoUtility.IsAntiTorpedo(__instance.GetLoadedMissileConfig());
            __result = torpedo == antiTorpedo;
        }
    }
}
