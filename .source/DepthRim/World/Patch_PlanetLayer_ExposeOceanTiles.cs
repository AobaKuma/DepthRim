using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海洋層格子資料的存讀檔。原版 <see cref="PlanetLayer.ExposeData"/> 只存生態區（地表層的 <c>SurfaceLayer</c> 才存高度、溫度等），
    /// 讀檔後海洋格的高度變成圖層半徑（正數），海床深度算成 0 m、年均水溫歸零（箱網、水溫、水深著色全部錯）。
    /// 這裡另外存高度（0.1 m）、年均溫（0.01 °C）與地勢；舊存檔沒有這些資料時記下來，
    /// 由 <see cref="WorldComponent_OceanTemperature.FinalizeInit"/> 用世界種子重算（<see cref="WorldGenStep_OceanTiles.RestoreTileData"/>）。
    /// </summary>
    [HarmonyPatch(typeof(PlanetLayer), nameof(PlanetLayer.ExposeData))]
    public static class Patch_PlanetLayer_ExposeOceanTiles
    {
        /// <summary>讀檔時存檔裡沒有格子資料的海洋層（舊存檔），等世界初始化完成後補算。</summary>
        public static PlanetLayer PendingRestore;

        private static byte[] tmpElevation;
        private static byte[] tmpTemperature;
        private static byte[] tmpHilliness;

        [HarmonyPostfix]
        public static void Postfix(PlanetLayer __instance)
        {
            if (__instance?.Def != DepthRimDefOf.DepthRim_Ocean)
            {
                return;
            }
            int count = __instance.TilesCount;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (__instance.Tiles == null || __instance.Tiles.Count != count)
                {
                    return;
                }
                tmpElevation = DataSerializeUtility.SerializeInt(count, i => Mathf.RoundToInt(__instance.Tiles[i].elevation * 10f));
                tmpTemperature = DataSerializeUtility.SerializeInt(count, i => Mathf.RoundToInt(__instance.Tiles[i].temperature * 100f));
                tmpHilliness = DataSerializeUtility.SerializeByte(count, i => (byte)__instance.Tiles[i].hilliness);
            }
            DataExposeUtility.LookByteArray(ref tmpElevation, "depthRimOceanElevation");
            DataExposeUtility.LookByteArray(ref tmpTemperature, "depthRimOceanTemperature");
            DataExposeUtility.LookByteArray(ref tmpHilliness, "depthRimOceanHilliness");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (Complete(tmpElevation, count * 4) && Complete(tmpTemperature, count * 4) && Complete(tmpHilliness, count)
                    && __instance.Tiles != null && __instance.Tiles.Count == count)
                {
                    DataSerializeUtility.LoadInt(tmpElevation, count, (i, v) => __instance.Tiles[i].elevation = v / 10f);
                    DataSerializeUtility.LoadInt(tmpTemperature, count, (i, v) => __instance.Tiles[i].temperature = v / 100f);
                    DataSerializeUtility.LoadByte(tmpHilliness, count, (i, v) => __instance.Tiles[i].hilliness = (Hilliness)v);
                }
                else
                {
                    PendingRestore = __instance;
                }
            }
            tmpElevation = tmpTemperature = tmpHilliness = null;
        }

        private static bool Complete(byte[] data, int length)
        {
            return data != null && data.Length >= length;
        }
    }
}
