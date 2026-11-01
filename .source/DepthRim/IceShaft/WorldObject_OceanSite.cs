using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 冰洋層上的地圖父物件（升降井下方的水下基地）。
    /// 原版 <see cref="SpaceMapParent"/> 在沒有人時會移除地圖；水下基地只要還有升降井出口或玩家建築就保留，
    /// 否則殖民者一回到地表，整個基地（和排乾的艙室）就消失了。
    /// </summary>
    public class WorldObject_OceanSite : SpaceMapParent
    {
        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            if (HasMap && (Map.listerBuildings.allBuildingsColonist.Count > 0 || Map.listerThings.AllThings.Any(t => t is PocketMapExit)))
            {
                return false;
            }
            return base.ShouldRemoveMapNow(out alsoRemoveWorldObject);
        }
    }
}
