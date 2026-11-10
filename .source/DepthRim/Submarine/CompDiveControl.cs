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
    /// 艦橋上的下潛控制：和潛艇世界物件上的指令是同一個（<see cref="WorldObject_Submarine.DiveGizmo"/>）。
    /// 還在船塢（地圖父物件不是潛艇）時顯示但停用。
    /// </summary>
    public class CompDiveControl : ThingComp
    {
        public WorldObject_Submarine Submarine => parent.Map?.Parent as WorldObject_Submarine;

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer)
            {
                yield break;
            }
            WorldObject_Submarine sub = Submarine;
            if (sub != null)
            {
                yield return sub.DiveGizmo();
                if (sub.InFlight && !sub.isImpact)
                {
                    yield return sub.StopGizmo();
                }
                yield break;
            }
            Command_Action disabled = new Command_Action
            {
                defaultLabel = "DepthRim.Sub.CruiseNone".Translate(),
                defaultDesc = "DepthRim.Sub.NotAboard".Translate(),
                icon = WorldObject_Submarine.DiveIcon,
                action = delegate { },
            };
            disabled.Disable("DepthRim.Sub.NotAboard".Translate());
            yield return disabled;
        }
    }
}
