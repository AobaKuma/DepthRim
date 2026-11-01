using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    public class CompProperties_DivingAir : CompProperties
    {
        /// <summary>滿氣瓶可在水中待的時間（ticks）。</summary>
        public int capacityTicks = 30000;

        /// <summary>離開水面時，每 tick 補充的氣量（ticks）。</summary>
        public float refillPerTick = 10f;

        public CompProperties_DivingAir()
        {
            compClass = typeof(CompDivingAir);
        }
    }

    /// <summary>
    /// 潛水裝備的氧氣（設計稿 §4.2「氧氣時間」）。由 <see cref="FloodingPawnEffects"/> 每次更新扣除或補充，本身不 tick。
    /// 工作效率、移動效率、水壓抗性由裝備的 equippedStatOffsets 提供。
    /// </summary>
    public class CompDivingAir : ThingComp
    {
        private float airTicks = -1f;

        public CompProperties_DivingAir Props => (CompProperties_DivingAir)props;

        public float Air
        {
            get
            {
                if (airTicks < 0f)
                {
                    airTicks = Props.capacityTicks;
                }
                return airTicks;
            }
        }

        public float AirPercent => Air / Props.capacityTicks;

        public static CompDivingAir WornBy(Pawn pawn)
        {
            List<Apparel> worn = pawn.apparel?.WornApparel;
            if (worn == null)
            {
                return null;
            }
            for (int i = 0; i < worn.Count; i++)
            {
                CompDivingAir comp = worn[i].GetComp<CompDivingAir>();
                if (comp != null)
                {
                    return comp;
                }
            }
            return null;
        }

        /// <summary>扣除 ticks 的氣量；氣瓶已空時回傳 false。</summary>
        public bool TryConsume(int ticks)
        {
            if (Air <= 0f)
            {
                return false;
            }
            airTicks = Mathf.Max(0f, Air - ticks);
            return true;
        }

        public void Refill(int ticks)
        {
            airTicks = Mathf.Min(Props.capacityTicks, Air + Props.refillPerTick * ticks);
        }

        public override string CompInspectStringExtra()
        {
            return "DepthRim.DivingAir.Inspect".Translate(AirPercent.ToStringPercent(), ((int)Air).ToStringTicksToPeriod());
        }

        public override IEnumerable<Gizmo> CompGetWornGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetWornGizmosExtra())
            {
                yield return g;
            }
            Pawn wearer = (parent as Apparel)?.Wearer;
            if (wearer != null && wearer.IsColonistPlayerControlled)
            {
                yield return new Gizmo_DivingAir(this);
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref airTicks, "airTicks", -1f);
        }
    }

    /// <summary>殖民者選取時顯示的氧氣條。</summary>
    [StaticConstructorOnStartup]
    public class Gizmo_DivingAir : Gizmo
    {
        private static readonly Texture2D FullBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.3f, 0.6f, 0.9f));

        private static readonly Texture2D EmptyBarTex = SolidColorMaterials.NewSolidColorTexture(Color.clear);

        private readonly CompDivingAir air;

        public Gizmo_DivingAir(CompDivingAir air)
        {
            this.air = air;
            Order = -100f;
        }

        public override float GetWidth(float maxWidth)
        {
            return 140f;
        }

        public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            Rect rect = new Rect(topLeft.x, topLeft.y, GetWidth(maxWidth), 75f);
            Widgets.DrawWindowBackground(rect);
            Rect inner = rect.ContractedBy(6f);
            Rect labelRect = new Rect(inner.x, inner.y, inner.width, inner.height / 2f);
            Text.Font = GameFont.Tiny;
            Widgets.Label(labelRect, "DepthRim.DivingAir.Label".Translate());
            Rect barRect = new Rect(inner.x, inner.y + inner.height / 2f, inner.width, inner.height / 2f);
            Widgets.FillableBar(barRect, air.AirPercent, FullBarTex, EmptyBarTex, doBorder: false);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(barRect, ((int)air.Air).ToStringTicksToPeriod());
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            return new GizmoResult(GizmoState.Clear);
        }
    }
}
