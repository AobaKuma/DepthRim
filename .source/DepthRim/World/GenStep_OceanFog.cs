using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 冰洋地圖的迷霧：整張地圖蓋上迷霧，再從一個連到地圖邊緣的開放水域格往外揭開（比照原版 GenStep_Fog 沒有玩家起點時的做法）。
    /// 原版 GenStep_Fog 會先讀 MapGenerator.PlayerStartSpot，非開局地圖沒有起點就會報錯，所以另寫一個。
    /// 結果：開放水域可見，岩體內部與封閉的洞穴保持迷霧；升降井出口另外從出口揭開（<see cref="Building_IceShaft"/>）。
    /// </summary>
    public class GenStep_OceanFog : GenStep
    {
        public override int SeedPart => 552810447;

        public override void Generate(Map map, GenStepParams parms)
        {
            map.fogGrid.Refog(CellRect.WholeMap(map));
            bool Valid(IntVec3 c) => c.Standable(map) && !c.Roofed(map)
                                     && map.reachability.CanReachMapEdge(c, TraverseParms.For(TraverseMode.NoPassClosedDoorsOrWater));
            if (CellFinder.TryFindRandomCellNear(map.Center, map, 30, Valid, out IntVec3 root)
                || CellFinder.TryFindRandomEdgeCellWith(Valid, map, 0f, out root)
                || CellFinder.TryFindRandomCell(map, Valid, out root))
            {
                FloodFillerFog.FloodUnfog(root, map);
            }
        }
    }
}
