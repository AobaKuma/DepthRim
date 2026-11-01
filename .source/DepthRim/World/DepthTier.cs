using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>水深層級（設計稿 §5）。數值越大越深。</summary>
    public enum DepthTier : byte
    {
        Shallow,
        Thermocline,
        Deep,
        Seabed
    }

    public static class DepthTierUtility
    {
        /// <summary>
        /// 海洋格子的水深層級：由格子的生態區決定（<see cref="WaterMapExtension.depthTier"/>），不存檔。
        /// 之後潛艇在這一格能下到多深、下錨的地圖在哪個層級，都以這個為準。
        /// </summary>
        public static DepthTier MaxTierAt(PlanetTile tile)
        {
            if (!tile.Valid)
            {
                return DepthTier.Shallow;
            }
            return tile.Tile?.PrimaryBiome?.GetModExtension<WaterMapExtension>()?.depthTier ?? DepthTier.Shallow;
        }

        public static string Label(this DepthTier tier)
        {
            return ("DepthRim.DepthTier." + tier).Translate();
        }
    }
}
