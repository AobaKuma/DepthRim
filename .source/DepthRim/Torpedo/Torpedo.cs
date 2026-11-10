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
    /// 魚雷：DMSE 飛彈換皮（設計稿 §11 M5）。彈體掛這個標記就是魚雷：只能走海路打海洋格（彈體的 engageWorker：<see cref="TorpedoEngageWorker"/>），
    /// 而且帶著發射時的深度（<see cref="MissileBehavior_Torpedo"/>），溫躍層遮蔽偵測。
    /// </summary>
    public class TorpedoExtension : DefModExtension
    {
    }

    /// <summary>反魚雷魚雷的彈體標記（內容檢查用）。攔截的分工由彈體的水下領域（DMSE 的 <c>CombatDomain</c>）決定。</summary>
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

        /// <summary>航跡上的魚雷行為（記著深度）；不是魚雷回傳 null。</summary>
        public static MissileBehavior_Torpedo BehaviorOf(object track)
        {
            return (track as MissileWorldObject)?.Behaviors?.Get<MissileBehavior_Torpedo>();
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

    /// <summary>
    /// 魚雷只走海路（DMSE 彈體的 <see cref="MissileEngageWorker"/>）：兩端都在海洋層、目標是海洋格、海路到得了，射程用海路距離。
    /// 取代 DMSE 的直線射程與跨圖層規則。
    /// </summary>
    public class TorpedoEngageWorker : MissileEngageWorker
    {
        public override bool CanEngage(MissileConfig config, PlanetTile from, PlanetTile to, float maxRange, out string reason)
        {
            int range = config.Range;
            if (maxRange > 0f && (range <= 0 || maxRange < range))
            {
                range = (int)maxRange;
            }
            string key = TorpedoUtility.BlockReason(from, to, range, out _);
            reason = key?.Translate(range);
            return key == null;
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

    /// <summary>魚雷彈體的行為：記下深度（<see cref="MissileBehavior_Torpedo"/>）。溫躍層遮蔽在 <see cref="SonarLayerModifier"/>。</summary>
    public class TorpedoBehaviorProps : MissileBehaviorProps
    {
        public TorpedoBehaviorProps()
        {
            behaviorClass = typeof(MissileBehavior_Torpedo);
        }
    }
}
