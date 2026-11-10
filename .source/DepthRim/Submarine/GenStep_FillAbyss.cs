using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇船體地圖（<c>DepthRim_SubmarineHull</c>）：整張鋪深淵。DMSE 分離時再把船體以相同座標搬進來。
    /// 深淵本身沒有屋頂、碰到地圖邊緣，所以艙外全是開放水域（<see cref="MapComponent_Flooding"/>）。
    /// </summary>
    public class GenStep_FillAbyss : GenStep
    {
        public override int SeedPart => 518403271;

        public override void Generate(Map map, GenStepParams parms)
        {
            TerrainDef abyss = DepthRimDefOf.DepthRim_Abyss;
            foreach (IntVec3 c in map.AllCells)
            {
                map.terrainGrid.SetTerrain(c, abyss);
            }
        }
    }
}
