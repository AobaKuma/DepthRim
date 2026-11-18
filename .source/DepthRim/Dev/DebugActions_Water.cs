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
            SectionLayer_VoyageDarkness.Reset();
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
            Messages.Message("DepthRim.Debug.WaterMaterialsReset".Translate(), MessageTypeDefOf.SilentInput, historical: false);
        }

        /// <summary>
        /// 叫一隻中層水域的生物穿越目前的地圖（不檢查層級，方便在任何地圖上看效果）。
        /// 牠只在深淵格露出來；畫面層只在變溫層與深海顯示，其他層級先用「Override water visual tier」切過去。
        /// </summary>
        [DebugAction("DepthRim", "Midwater creature passes", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void MidwaterCreaturePasses()
        {
            MapComponent_Midwater comp = Find.CurrentMap?.GetComponent<MapComponent_Midwater>();
            if (comp == null)
            {
                return;
            }
            options.Clear();
            foreach (MidwaterCreatureDef def in DefDatabase<MidwaterCreatureDef>.AllDefsListForReading)
            {
                MidwaterCreatureDef captured = def;
                options.Add(new DebugMenuOption(captured.defName, DebugMenuOptionMode.Action, delegate
                {
                    comp.StartPass(captured);
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options, "Midwater creature"));
        }

        /// <summary>在點選的格子引爆，檢查水中內爆（Docs/水中內爆_實作計畫.md）。乾地、半淹與排除類型會照原版爆炸。</summary>
        [DebugAction("DepthRim", "Explosion: bomb r3", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ExplodeBomb()
        {
            Explode(DamageDefOf.Bomb, 3f);
        }

        [DebugAction("DepthRim", "Explosion: flame r3", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ExplodeFlame()
        {
            Explode(DamageDefOf.Flame, 3f);
        }

        [DebugAction("DepthRim", "Explosion: EMP r3", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ExplodeEmp()
        {
            Explode(DamageDefOf.EMP, 3f);
        }

        private static void Explode(DamageDef damage, float radius)
        {
            Map map = Find.CurrentMap;
            IntVec3 cell = UI.MouseCell();
            if (map == null || !cell.InBounds(map))
            {
                return;
            }
            GenExplosion.DoExplosion(cell, map, radius, damage, null);
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
