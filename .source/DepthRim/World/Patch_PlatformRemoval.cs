using DMSE;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇船體平台（<c>DepthRim_HullPlatform</c>）沿用 DMSE 的「拆除平台」指令。DMSE 拆完一律把地形改回太空虛空
    /// （<see cref="PlatformRemovalUtility.Remove"/> 最後一行 <c>SetTerrain(c, TerrainDefOf.Space)</c>）；
    /// 在海洋層的地圖上，平台底下其實是深淵，所以拆完立刻改回深淵。
    /// 同一個呼叫裡就換掉，中間不會經過任何 tick，太空地形不會有機會造成真空。
    /// </summary>
    [HarmonyPatch(typeof(PlatformRemovalUtility), nameof(PlatformRemovalUtility.Remove))]
    public static class Patch_PlatformRemoval
    {
        /// <summary>這張地圖上拆掉平台後該露出什麼：海洋層是深淵，其他地圖照 DMSE（null = 不改）。</summary>
        public static TerrainDef RestoredTerrain(Map map)
        {
            return map?.Tile.Valid == true && map.Tile.LayerDef == DepthRimDefOf.DepthRim_Ocean ? DepthRimDefOf.DepthRim_Abyss : null;
        }

        [HarmonyPostfix]
        public static void Postfix(IntVec3 c, Map map)
        {
            TerrainDef restored = RestoredTerrain(map);
            if (restored != null && c.InBounds(map) && map.terrainGrid.TerrainAt(c) == TerrainDefOf.Space)
            {
                map.terrainGrid.SetTerrain(c, restored);
            }
        }
    }
}
