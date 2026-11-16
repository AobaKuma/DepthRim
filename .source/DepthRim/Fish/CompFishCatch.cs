using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DepthRim
{
    public class CompProperties_FishCatch : CompProperties
    {
        public FishSize size = FishSize.Medium;

        public CompProperties_FishCatch()
        {
            compClass = typeof(CompFishCatch);
        }
    }

    /// <summary>
    /// 通用魚類生物的品種：第一次出現在地圖上時依當地生態區與水溫抽一個 <see cref="FishVariantDef"/>（存檔），
    /// 決定牠的顏色（<see cref="PawnRenderNode_FishBody"/>）、名稱（<see cref="Patch_FishKindLabel"/>）
    /// 與死後的魚獲（<see cref="DeathActionWorker_FishCatch"/>）。
    /// 有這個 comp 的種族也視為水生：不會溺水、不受水壓（<see cref="IsAquatic"/>）。
    /// </summary>
    public class CompFishCatch : ThingComp
    {
        private FishVariantDef variant;

        public CompProperties_FishCatch Props => (CompProperties_FishCatch)props;

        public FishVariantDef Variant => variant;

        public void SetVariant(FishVariantDef def)
        {
            variant = def;
            if (parent is Pawn pawn && pawn.Drawer?.renderer != null)
            {
                pawn.Drawer.renderer.SetAllGraphicsDirty();
            }
        }

        /// <summary>還沒有品種就照地圖抽一個（地圖可為 null：只依體型）。</summary>
        public FishVariantDef EnsureVariant(Map map)
        {
            if (variant == null)
            {
                SetVariant(FishVariantSelector.ChooseFor(Props.size, map));
            }
            return variant;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            EnsureVariant(parent.Map);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Defs.Look(ref variant, "fishVariant");
        }

        public override string CompInspectStringExtra()
        {
            if (variant?.catchDef == null)
            {
                return null;
            }
            return "DepthRim.FishCatch".Translate(variant.catchDef.label, variant.catchCount.min, variant.catchCount.max);
        }

        public static bool IsAquatic(Pawn pawn)
        {
            return pawn?.def?.GetCompProperties<CompProperties_FishCatch>() != null;
        }
    }

    /// <summary>魚死了不留屍體，直接變成品種的魚獲（保留屍體的禁用狀態，獵人打到的就不禁用）。</summary>
    public class DeathActionWorker_FishCatch : DeathActionWorker
    {
        public override void PawnDied(Corpse corpse, Lord prevLord)
        {
            Map map = corpse.MapHeld;
            if (map == null)
            {
                return;
            }
            IntVec3 cell = corpse.PositionHeld;
            Pawn fish = corpse.InnerPawn;
            FishVariantDef variant = fish?.TryGetComp<CompFishCatch>()?.EnsureVariant(map);
            if (variant?.catchDef == null)
            {
                return;
            }
            bool forbidden = corpse.IsForbidden(Faction.OfPlayer);
            corpse.Destroy();
            int remaining = variant.catchCount.RandomInRange;
            while (remaining > 0)
            {
                Thing item = ThingMaker.MakeThing(variant.catchDef);
                item.stackCount = Mathf.Min(remaining, variant.catchDef.stackLimit);
                remaining -= item.stackCount;
                if (GenPlace.TryPlaceThing(item, cell, map, ThingPlaceMode.Near, out Thing placed))
                {
                    placed.SetForbidden(forbidden, warnOnFail: false);
                }
            }
        }
    }

    /// <summary>魚的身體：通用白色貼圖乘上品種顏色。</summary>
    public class PawnRenderNode_FishBody : PawnRenderNode_AnimalPart
    {
        public PawnRenderNode_FishBody(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree) : base(pawn, props, tree)
        {
        }

        public override Graphic GraphicFor(Pawn pawn)
        {
            Graphic graphic = base.GraphicFor(pawn);
            FishVariantDef variant = pawn.TryGetComp<CompFishCatch>()?.Variant;
            if (graphic == null || variant == null)
            {
                return graphic;
            }
            Color color = variant.color;
            if (pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Rotting)
            {
                color = PawnRenderUtility.GetRottenColor(color);
            }
            return graphic.GetColoredVersion(graphic.Shader, color, color);
        }
    }

    /// <summary>魚的名稱用品種的名稱（例如「鱈魚」），而不是通用的「小型魚」。</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.KindLabel), MethodType.Getter)]
    public static class Patch_FishKindLabel
    {
        public static void Postfix(Pawn __instance, ref string __result)
        {
            FishVariantDef variant = __instance.TryGetComp<CompFishCatch>()?.Variant;
            if (variant != null && !variant.label.NullOrEmpty())
            {
                __result = variant.label;
            }
        }
    }
}
