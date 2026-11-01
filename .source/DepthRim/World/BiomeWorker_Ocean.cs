using RimWorld;
using RimWorld.Planet;

namespace DepthRim
{
    /// <summary>
    /// 海洋層的七種生態區共用：只存在於海洋層，地表世界生成時不會被選到。
    /// 海洋層的格子由 <see cref="WorldGenStep_OceanTiles"/> 直接指定生態區，不走原版的評分。
    /// </summary>
    public class BiomeWorker_Ocean : BiomeWorker
    {
        public override bool CanPlaceOnLayer(BiomeDef biome, PlanetLayer layer)
        {
            return layer.Def == DepthRimDefOf.DepthRim_Ocean;
        }

        public override float GetScore(BiomeDef biome, Tile tile, PlanetTile planetTile)
        {
            return -100f;
        }
    }
}
