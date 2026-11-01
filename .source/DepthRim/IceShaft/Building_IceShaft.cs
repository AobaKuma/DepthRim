using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 冰層升降井（設計稿 §4.2）：蓋在冰蓋地表，連到正下方冰洋層格子的地圖。
    /// 沿用原版 <see cref="MapPortal"/> 的進出流程（進入視窗、搬運、穿越），只把「口袋地圖」換成冰洋層上的真實地圖，
    /// 所以冰洋基地有自己的世界格子，之後潛艇可以從這裡出航（DMSE 轉移飛行以圖層判定起飛）。
    /// 同一個冰洋格子已經有地圖時（例如另一座升降井）會共用，只另外放一個井底出口。
    /// </summary>
    public class Building_IceShaft : MapPortal
    {
        private const float ExitClearRadius = 2.9f;

        protected override Map GeneratePocketMapInt()
        {
            PlanetLayer ocean = Find.WorldGrid.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean);
            if (ocean == null)
            {
                Log.Error("[DepthRim] Ice shaft could not find the ice ocean planet layer.");
                return null;
            }
            PlanetTile tile = ocean.GetClosestTile_NewTemp(Map.Tile);
            if (Find.WorldObjects.MapParentAt(tile) == null)
            {
                MapParent site = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
                site.Tile = tile;
                site.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(site);
            }
            Map oceanMap = GetOrGenerateMapUtility.GetOrGenerateMap(tile, Map.Size, DepthRimDefOf.DepthRim_OceanSite);
            if (oceanMap != null)
            {
                SpawnExit(oceanMap);
            }
            return oceanMap;
        }

        /// <summary>
        /// 在冰洋地圖上對應的位置放井底出口並清出周圍岩體。
        /// 由 <see cref="MapPortal"/> 的 GeneratePocketMap 呼叫，期間 currentlyGeneratingPortal 指向本井，
        /// 所以出口的 <see cref="PocketMapExit.SpawnSetup"/> 會自動接上入口。
        /// </summary>
        private void SpawnExit(Map oceanMap)
        {
            ThingDef exitDef = def.portal?.exitDef;
            if (exitDef == null)
            {
                Log.Error($"[DepthRim] {def.defName} has no portal.exitDef.");
                return;
            }
            CellRect bounds = CellRect.WholeMap(oceanMap).ContractedBy(8);
            IntVec3 cell = Position.ClampInsideRect(bounds);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(cell, ExitClearRadius, useCenter: true))
            {
                if (!c.InBounds(oceanMap))
                {
                    continue;
                }
                Building edifice = c.GetEdifice(oceanMap);
                if (edifice != null && edifice.def.mineable)
                {
                    edifice.Destroy(DestroyMode.Vanish);
                }
            }
            GenSpawn.Spawn(exitDef, cell, oceanMap, WipeMode.Vanish);
            FloodFillerFog.FloodUnfog(cell, oceanMap);
        }
    }
}
