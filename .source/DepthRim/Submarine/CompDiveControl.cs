using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DepthRim
{
    public class CompProperties_DiveControl : CompProperties
    {
        public CompProperties_DiveControl()
        {
            compClass = typeof(CompDiveControl);
        }
    }

    /// <summary>
    /// 艦橋上的航行指令（<see cref="SubmarineNavigation"/>），和潛艇世界物件上的是同一個。
    /// 也記著航行指令規劃的終端深度：啟航後新的潛艇第一次 tick 時接手（<see cref="TakeVoyageDepth"/>）。
    /// </summary>
    public class CompDiveControl : ThingComp
    {
        private bool hasVoyageDepth;

        private DepthTier voyageStart;

        private DepthTier voyageEnd;

        public WorldObject_Submarine Submarine => parent.Map?.Parent as WorldObject_Submarine;

        public void SetVoyageDepth(DepthTier start, DepthTier end)
        {
            hasVoyageDepth = true;
            voyageStart = start;
            voyageEnd = end;
        }

        /// <summary>取走規劃的出發與終端深度（取走後清掉）。</summary>
        public bool TakeVoyageDepth(out DepthTier start, out DepthTier end)
        {
            start = voyageStart;
            end = voyageEnd;
            bool had = hasVoyageDepth;
            hasVoyageDepth = false;
            return had;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer)
            {
                yield break;
            }
            if (CoastalLaunch.IsShipyardMap(parent.Map))
            {
                // 原版海岸地圖上的船：只有下水（航行要先到海上，M2）
                yield return CoastalLaunch.LaunchGizmo(parent);
                yield break;
            }
            yield return SubmarineNavigation.NavigateGizmo(parent);
            WorldObject_Submarine sub = Submarine;
            if (sub != null)
            {
                if (sub.InFlight && !sub.isImpact)
                {
                    yield return sub.StopGizmo();
                }
                else if (!sub.landingTarget.Valid)
                {
                    foreach (Gizmo g in SubmarineSurfacing.SubmarineGizmos(sub))
                    {
                        yield return g;
                    }
                }
                yield break;
            }
            if (SubmarineSurfacing.IsSurfacedMap(parent.Map))
            {
                foreach (Gizmo g in SubmarineSurfacing.SurfacedGizmos(parent))
                {
                    yield return g;
                }
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref hasVoyageDepth, "hasVoyageDepth");
            Scribe_Values.Look(ref voyageStart, "voyageStart");
            Scribe_Values.Look(ref voyageEnd, "voyageEnd");
        }
    }
}
