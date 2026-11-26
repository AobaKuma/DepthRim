using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 聲納發現地標：每小時對每一具玩家的聲納（DMSE BVR 網路裡玩家的節點）擲骰，範圍越大越常發現。
    /// 主動和被動聲納找得到的種類一樣，只差範圍（溫躍層遮蔽照 DMSE 的修正鏈）。
    /// 新地標放在範圍內、海路到得了、沒有其他世界物件的海洋格，依那格的海床層級挑種類。
    /// </summary>
    public class WorldComponent_OceanLandmarks : WorldComponent
    {
        public const int CheckInterval = GenDate.TicksPerHour;

        public WorldComponent_OceanLandmarks(World world) : base(world)
        {
        }

        public override void WorldComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckInterval != 17)
            {
                return;
            }
            WorldComponent_AirDefenseNetwork network = WorldComponent_AirDefenseNetwork.Current;
            if (network == null)
            {
                return;
            }
            foreach (AirDefenseNode node in network.Nodes.ToList())
            {
                if (node.faction != Faction.OfPlayer || node.parent == null)
                {
                    continue;
                }
                float range = node.DetectionRange(AirTargetExtension.Default);
                if (Rand.Chance(LandmarkMath.DiscoveryChancePerHour(range)))
                {
                    TryDiscover(node, range, out _);
                }
            }
        }

        /// <summary>這具聲納在海洋層上的中心：海洋層的地圖就是它的格子，海面地圖用正下方的海洋格。</summary>
        public static PlanetTile OceanCenter(MapParent parent)
        {
            if (parent == null)
            {
                return PlanetTile.Invalid;
            }
            if (OceanRouting.IsOcean(parent.Tile))
            {
                return parent.Tile;
            }
            PlanetTile below = OceanWorldView.TileBelow(parent.Tile);
            return OceanRouting.IsOcean(below) ? below : PlanetTile.Invalid;
        }

        public static int CountWithin(PlanetTile center, float range)
        {
            Vector3 at = MissileWorldUtility.TileCenter(center);
            return OceanLandmarkUtility.AllLandmarks.Count(o => MissileWorldUtility.SurfaceTilesBetween(at, MissileWorldUtility.TileCenter(o.Tile)) <= range);
        }

        /// <summary>範圍內可以放地標的格子：海路到得了、不是中心本身、沒有任何世界物件。</summary>
        public static List<PlanetTile> CandidateTiles(PlanetTile center, float range)
        {
            List<PlanetTile> result = new List<PlanetTile>();
            if (!center.Valid || range <= 0f)
            {
                return result;
            }
            Vector3 at = MissileWorldUtility.TileCenter(center);
            int steps = Mathf.CeilToInt(range) + 1;
            HashSet<PlanetTile> seen = new HashSet<PlanetTile> { center };
            List<PlanetTile> frontier = new List<PlanetTile> { center };
            List<PlanetTile> neighbors = new List<PlanetTile>();
            for (int step = 0; step < steps && frontier.Count > 0; step++)
            {
                List<PlanetTile> next = new List<PlanetTile>();
                foreach (PlanetTile t in frontier)
                {
                    neighbors.Clear();
                    Find.WorldGrid.GetTileNeighbors(t, neighbors);
                    foreach (PlanetTile n in neighbors)
                    {
                        if (!seen.Add(n) || !OceanRouting.Navigable(n))
                        {
                            continue;
                        }
                        if (MissileWorldUtility.SurfaceTilesBetween(at, MissileWorldUtility.TileCenter(n)) > range)
                        {
                            continue;
                        }
                        next.Add(n);
                        if (!Find.WorldObjects.AnyWorldObjectAt(n))
                        {
                            result.Add(n);
                        }
                    }
                }
                frontier = next;
            }
            return result;
        }

        public static bool TryDiscover(AirDefenseNode node, float range, out WorldObject landmark)
        {
            landmark = null;
            PlanetTile center = OceanCenter(node.parent);
            if (!center.Valid)
            {
                return false;
            }
            if (!LandmarkMath.CanDiscover(CountWithin(center, range), OceanLandmarkUtility.AllLandmarks.Count()))
            {
                return false;
            }
            return TryPlace(center, range, null, out landmark);
        }

        /// <summary>在 <paramref name="center"/> 的範圍內放一個地標；<paramref name="forced"/> 指定種類（除錯與測試用）。</summary>
        public static bool TryPlace(PlanetTile center, float range, OceanLandmarkDef forced, out WorldObject landmark)
        {
            landmark = null;
            foreach (PlanetTile tile in CandidateTiles(center, range).InRandomOrder())
            {
                DepthTier tier = WorldObject_Submarine.SeafloorAt(tile);
                OceanLandmarkDef def = forced;
                if (def == null && !OceanLandmarkDef.AllAvailable.Where(d => d.AllowsTier(tier)).TryRandomElementByWeight(d => d.commonality, out def))
                {
                    continue;
                }
                if (forced != null && !forced.AllowsTier(tier))
                {
                    continue;
                }
                landmark = Spawn(def, tile);
                return landmark != null;
            }
            return false;
        }

        public static WorldObject Spawn(OceanLandmarkDef def, PlanetTile tile)
        {
            WorldObject o = WorldObjectMaker.MakeWorldObject(def.worldObjectDef);
            o.Tile = tile;
            int expire = Find.TickManager.TicksGame + def.lifetimeDays.RandomInRange * GenDate.TicksPerDay;
            if (o is WorldObject_OceanLandmark encounter)
            {
                encounter.landmarkDef = def;
                encounter.expireTick = expire;
            }
            else if (o is WorldObject_LandmarkSite site)
            {
                site.landmarkDef = def;
                site.expireTick = expire;
            }
            Find.WorldObjects.Add(o);
            Messages.Message("DepthRim.Landmark.Discovered".Translate(), o, MessageTypeDefOf.NeutralEvent);
            return o;
        }
    }
}
