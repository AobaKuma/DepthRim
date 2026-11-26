using DMSE;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇降落到海洋地點（下錨，包括開回自己的基地）也要守下錨規則：那裡要有能下錨的底（淺海或海床），
    /// 潛艇也要正停在那個深度。DMSE 的「降落」指令與抵達後的自動降落都會問目的地圖層的 <see cref="TransferLandingWorker"/>。
    /// </summary>
    public class SubmarineLandingWorker : TransferLandingWorker
    {
        public override string LandingBlockReason(WorldObject_TransferShip ship, PlanetTile target)
        {
            if (!(ship is WorldObject_Submarine sub) || !OceanRouting.IsOcean(target))
            {
                return null;
            }
            DepthTier site = SiteSeafloor(target);
            DiveMath.AnchorBlock block = DiveMath.CanAnchor(sub.cruiseTier, sub.targetTier, site);
            if (block == DiveMath.AnchorBlock.None)
            {
                return null;
            }
            string key = block == DiveMath.AnchorBlock.NoBottom ? "DepthRim.Anchor.Block.NoBottom" : "DepthRim.Anchor.Block.NotAtBottom";
            return key.Translate(site.Label());
        }

        /// <summary>地點的海床層級：有地圖就看地圖（地圖父物件可能自己提供層級），否則看那一格的生態區。</summary>
        public static DepthTier SiteSeafloor(PlanetTile tile)
        {
            MapParent parent = Find.WorldObjects.MapParentAt(tile);
            if (parent != null && parent.HasMap)
            {
                return FloodingUtility.DepthTierOf(parent.Map);
            }
            return WorldObject_Submarine.SeafloorAt(tile);
        }
    }
}
