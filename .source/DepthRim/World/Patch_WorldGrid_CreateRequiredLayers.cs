using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 確保世界上有地下冰洋圖層（設計稿 §4、DMSE_對接研究 §4）。
    /// 原版 <see cref="WorldGrid"/> 只依劇本的 <see cref="ScenPart_PlanetLayer"/> 建立圖層；DepthRim 不改劇本，
    /// 而是在建立完劇本圖層後補上冰洋層，所以任何劇本、舊存檔都能用。
    /// <list type="bullet">
    /// <item>新世界：在這裡註冊，之後由 <see cref="WorldGenerator.GenerateWorld"/> 的圖層迴圈生成格子。</item>
    /// <item>讀檔（PostLoadInit）且存檔裡沒有冰洋層：註冊後立即生成格子，接下來原版 StandardizeTileData 會保留這些格子。</item>
    /// </list>
    /// 冰洋層刻意不加入圖層連線（AddConnection），避免逆重船或穿梭機直接飛進冰洋；只能經由升降井進入。
    /// </summary>
    [HarmonyPatch(typeof(WorldGrid), "CreateRequiredLayers")]
    public static class Patch_WorldGrid_CreateRequiredLayers
    {
        public const string LayerTag = "DepthRim_Ocean";

        [HarmonyPostfix]
        public static void Postfix(WorldGrid __instance)
        {
            if (__instance.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean) != null)
            {
                return;
            }
            PlanetLayer ocean = __instance.RegisterPlanetLayer(DepthRimDefOf.DepthRim_Ocean, DepthRimDefOf.DepthRim_Ocean_Settings.settings);
            ocean.ScenarioTag = LayerTag;

            // 從地表放大鏡頭會潛入冰洋，從冰洋縮小回到地表
            PlanetLayer surface = __instance.Surface;
            if (surface != null)
            {
                if (surface.zoomInToLayer == null)
                {
                    surface.zoomInToLayer = ocean;
                }
                ocean.zoomOutToLayer = surface;
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                string seedString = Current.Game?.World?.info?.seedString ?? LayerTag;
                WorldGenerator.GeneratePlanetLayer(ocean, seedString, GenText.StableStringHash(seedString) ^ ocean.LayerID);
                Log.Message($"[DepthRim] Added ice ocean layer to an existing save ({ocean.TilesCount} tiles).");
            }
        }
    }
}
