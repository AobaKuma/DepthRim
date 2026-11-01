using HarmonyLib;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水體顯示（<see cref="SectionLayer_WaterVolume"/>）的開關，放在右下角的地圖顯示選項列（和原版溫度、真空覆蓋層同一排）。
    /// 只在水下地圖顯示按鈕。Pawn 的水線（<see cref="PawnWaterRendering"/>）不受影響。
    /// </summary>
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class FloodingOverlay
    {
        public static bool Show = true;

        [HarmonyPostfix]
        public static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || row == null || !FloodingUtility.IsWaterMap(Find.CurrentMap))
            {
                return;
            }
            // TODO: 專用圖示，暫借原版真空覆蓋層的按鈕
            bool before = Show;
            row.ToggleableIcon(ref Show, TexButton.ShowVacuumOverlay, "DepthRim.Overlay.Toggle".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            if (Show && !before)
            {
                // 隱藏期間原版不會重建不可見的區塊層，重新顯示時整張地圖重建一次
                Find.CurrentMap.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
            }
        }
    }
}
