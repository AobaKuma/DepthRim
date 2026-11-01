using System;
using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 除錯選單（實作指南 C.3）：手動把某張地圖的水體視覺層級固定成四種之一，
    /// 不必真的把潛艇開到深海就能比較各層級的畫面。只影響畫面（<see cref="MapComponent_Flooding.RenderTier"/>），
    /// 水壓、滲水速度等遊戲數值仍然照地圖實際的層級走。
    /// </summary>
    public static class DebugActions_Water
    {
        private static readonly List<DebugMenuOption> options = new List<DebugMenuOption>();

        [DebugAction("DepthRim", "Override water visual tier", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void OverrideWaterTier()
        {
            Map map = Find.CurrentMap;
            MapComponent_Flooding flooding = map?.Flooding();
            if (flooding == null)
            {
                return;
            }
            options.Clear();
            options.Add(new DebugMenuOption("Auto (" + flooding.DepthTier.Label() + ")", DebugMenuOptionMode.Action, delegate
            {
                Apply(flooding, null);
            }));
            foreach (DepthTier tier in Enum.GetValues(typeof(DepthTier)))
            {
                DepthTier captured = tier;
                options.Add(new DebugMenuOption(captured.Label(), DebugMenuOptionMode.Action, delegate
                {
                    Apply(flooding, captured);
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options, "Water visual tier"));
        }

        [DebugAction("DepthRim", "Reset water materials", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ResetWaterMaterials()
        {
            WaterTierMaterials.Reset();
            SectionLayer_AbyssBackdrop.Reset();
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
            Messages.Message("DepthRim.Debug.WaterMaterialsReset".Translate(), MessageTypeDefOf.SilentInput, historical: false);
        }

        private static void Apply(MapComponent_Flooding flooding, DepthTier? tier)
        {
            if (flooding == null || flooding.map == null)
            {
                return;
            }
            flooding.debugTierOverride = tier;
            flooding.map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
            Messages.Message("DepthRim.Debug.WaterTier".Translate(tier?.Label() ?? "auto"), MessageTypeDefOf.SilentInput, historical: false);
        }
    }
}
