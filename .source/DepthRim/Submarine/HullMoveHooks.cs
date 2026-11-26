using DMSE;
using Fortified;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// DMSE 搬船體（出港分離到船體地圖、下錨或上浮降落到別張地圖）時，DepthRim 的逐格資料跟著船體搬：
    /// 艙室水位（新的船體地圖整張是深淵的滿水，不搬的話抽乾的艙室一出港就全部淹回去）與艙壓（原地歸零）。
    /// 掛在 DMSE 的 <see cref="ShipSeparationUtility.HullPlaced"/>：船體剛放到新地圖、原地圖的資料還沒被下一次更新改掉。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class HullMoveHooks
    {
        private static Map lastMap;

        private static IntVec3 lastOffset;

        static HullMoveHooks()
        {
            ShipSeparationUtility.HullPlaced -= OnHullPlaced;
            ShipSeparationUtility.HullPlaced += OnHullPlaced;
        }

        /// <summary>給測試：最近一次搬到潛艇地圖的位移（出港時 DMSE 把船體置中，見海洋層的 centerHullOnShipMap）。</summary>
        public static IntVec3 LastOffsetTo(Map map) => map == lastMap ? lastOffset : IntVec3.Zero;

        private static void OnHullPlaced(ShipSeparationUtility.HullMove move)
        {
            if (move.to?.Parent is WorldObject_Submarine)
            {
                lastMap = move.to;
                lastOffset = move.offset;
            }
            CarryWater(move);
            CarryPressure(move);
        }

        private static void CarryWater(ShipSeparationUtility.HullMove move)
        {
            MapComponent_Flooding to = move.to?.GetComponent<MapComponent_Flooding>();
            MapComponent_Flooding from = move.from?.GetComponent<MapComponent_Flooding>();
            if (from == null || from == to)
            {
                return;
            }
            if (to != null)
            {
                to.CopyLevels(from, move.cells, move.offset);
                FloodingUtility.WaterChanged(move.to);
            }
            // 原地圖空出來的船形：先搬完水位再灌，離港過場（遊戲暫停）期間外海層才不會留一個坑
            from?.FillVacated(move.cells);
        }

        private static void CarryPressure(ShipSeparationUtility.HullMove move)
        {
            MapComponent_PressureChambers from = move.from?.GetComponent<MapComponent_PressureChambers>();
            MapComponent_PressureChambers to = move.to?.GetComponent<MapComponent_PressureChambers>();
            if (from == null || to == null || from == to || !from.AnyPressure)
            {
                return;
            }
            to.CopyFrom(from, move.cells, move.offset);
        }
    }
}
