using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DepthRim
{
    public class CompProperties_IceBreakerFrame : CompProperties
    {
        /// <summary>可破冰厚上限（m）。</summary>
        public float maxThickness = 2f;

        public CompProperties_IceBreakerFrame()
        {
            compClass = typeof(CompIceBreakerFrame);
        }
    }

    /// <summary>
    /// 破冰結構（設計稿 §2.2）：船艙內頂住艙頂的加強肋材與千斤頂。有它才能破冰上浮，冰層開裂從它的中心開始；
    /// 血量低於一半時可破冰厚上限減半。不是船殼，不影響船殼耐壓。
    /// </summary>
    public class CompIceBreakerFrame : ThingComp
    {
        public CompProperties_IceBreakerFrame Props => (CompProperties_IceBreakerFrame)props;

        public float Limit => IceBreachMath.FrameLimit(Props.maxThickness, parent.HitPoints / (float)parent.MaxHitPoints);

        public static List<CompIceBreakerFrame> On(Map map)
        {
            if (map == null)
            {
                return new List<CompIceBreakerFrame>();
            }
            return map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompIceBreakerFrame>()).Where(c => c != null).ToList();
        }

        /// <summary>船上最強的破冰結構能破多厚（沒有破冰結構時 0）。</summary>
        public static float BestLimit(List<CompIceBreakerFrame> frames)
        {
            float best = 0f;
            foreach (CompIceBreakerFrame f in frames)
            {
                best = f.Limit > best ? f.Limit : best;
            }
            return best;
        }

        /// <summary>每座破冰結構受同樣的鈍擊傷害（已經平分過）。</summary>
        public static void Damage(List<CompIceBreakerFrame> frames, float perFrame)
        {
            if (perFrame <= 0f)
            {
                return;
            }
            foreach (CompIceBreakerFrame f in frames)
            {
                if (f.parent.Spawned && !f.parent.Destroyed)
                {
                    f.parent.TakeDamage(new DamageInfo(DamageDefOf.Blunt, perFrame));
                }
            }
        }

        public override string CompInspectStringExtra()
        {
            return "DepthRim.IceBreach.FrameLimit".Translate(Limit.ToString("0.0"));
        }
    }

    /// <summary>破冰結構只能蓋在有屋頂的船艙裡（它要頂住艙頂）。</summary>
    public class PlaceWorker_IceBreakerFrame : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, checkingDef.Size))
            {
                if (!c.InBounds(map) || !c.Roofed(map))
                {
                    return "DepthRim.IceBreach.NeedsRoof".Translate();
                }
            }
            return true;
        }
    }
}
