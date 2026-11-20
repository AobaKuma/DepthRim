using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇船殼的耐壓等級：所有「一面朝外海」的艙壁（牆、門、標了耐壓等級的建築）中最弱的那一個。
    /// 沒有 <see cref="LeakResistanceExtension"/> 的一般牆與門只算淺海。完全沒有朝外的艙壁（開放甲板）時不限制。
    ///
    /// <para>不是門檻（M6，設計稿 §2.2）：比它深時航行選單先跳確認（<see cref="HullWarning"/>），在那裡時顯示超載；
    /// 艙壁實際被壓壞由 <see cref="MapComponent_WallPressure"/> 處理。</para>
    /// </summary>
    public static class HullPressure
    {
        public const DepthTier Unlimited = DepthTier.Seabed;

        /// <summary>單一建築能承受的層級：有 <see cref="LeakResistanceExtension"/> 用它的值，其他一律淺海。</summary>
        public static DepthTier RatingOf(ThingDef def)
        {
            return def?.GetModExtension<LeakResistanceExtension>()?.pressureRating ?? DepthTier.Shallow;
        }

        /// <summary>
        /// 這個建築是不是船殼的一部分：牆、門，或明確標了耐壓等級的建築（<see cref="LeakResistanceExtension"/>）。
        /// 推進器、龍骨框架這類裝在船體上的機械不算——它們不圍出艙室，不該把整艘船的耐壓拉到淺海。
        /// </summary>
        public static bool IsHullPiece(Building b)
        {
            return b != null && (b.def.building?.isWall == true || b is Building_Door || b.def.HasModExtension<LeakResistanceExtension>());
        }

        /// <summary>整艘潛艇（船體地圖）的耐壓等級，並回傳最弱的那一塊（沒有時 null）。</summary>
        public static DepthTier RatingFor(Map map, out Building weakest)
        {
            weakest = null;
            MapComponent_Flooding flooding = map?.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return Unlimited;
            }
            DepthTier rating = Unlimited;
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                Building b = buildings[i];
                if (!IsHullPiece(b) || !FacesOpenWater(b, map, flooding))
                {
                    continue;
                }
                DepthTier r = RatingOf(b.def);
                if (weakest == null || r < rating)
                {
                    rating = r;
                    weakest = b;
                }
            }
            return weakest == null ? Unlimited : rating;
        }

        private static bool FacesOpenWater(Building b, Map map, MapComponent_Flooding flooding)
        {
            foreach (IntVec3 c in GenAdj.CellsAdjacentCardinal(b))
            {
                if (!c.InBounds(map) || c.GetEdifice(map) != null)
                {
                    continue;
                }
                // 深淵格不可通行、不屬於任何房間（沒有開放水域旗標），但它就是外海
                if (map.terrainGrid.TerrainAt(c) == DepthRimDefOf.DepthRim_Abyss || flooding.IsOpenAt(c))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
