using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 平台底下的暗化層（<see cref="PlatformShadow"/>：天光遮蔽、陽光投影、支柱的影子、水面層頭上的甲板輪廓），
    /// 蓋在平台底下的海面、浮船、建築與 Pawn 上；由 DepthRim_PlatformShadow.frag.glsl 依遮罩與每幀的太陽算。
    /// <list type="bullet">
    /// <item>水面層：整張地圖都蓋，排在建築與 Pawn（2900）之後、往上伸的柱身（2910）之前（柱身自己依高度算影子）。</item>
    /// <item>平台層：只蓋高空格（往下看得到海的地方），排在往下看的快照（1940）之後、往下伸的柱身（1950）與甲板（2000）之前。</item>
    /// </list>
    /// 頂點色 a = 1 − 燈光照亮影子的程度（<see cref="PlatformShadow.LampColor"/>，水面層的人造光），燈光改變（GroundGlow）時重建；
    /// 平台層由 <see cref="Patch_SeaLevelChanged"/> 把水面層的燈光變化轉過來。
    /// </summary>
    public class SectionLayer_PlatformShadow : SectionLayer
    {
        /// <summary>給驗收測試：最近一次重建蓋了幾格。</summary>
        public int lastCells;

        public SectionLayer_PlatformShadow(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.GroundGlow;
        }

        public override bool Visible => true;

        /// <summary>這一格要不要蓋影子：水面層每一格都要，平台層只有高空格。</summary>
        public static bool Covers(IntVec3 c, Map map)
        {
            if (SeaLevels.IsSeaLevel(map))
            {
                return true;
            }
            return SeaLevels.IsPlatformLevel(map) && map.terrainGrid.TerrainAt(c) == DepthRimDefOf.DepthRim_OpenAir;
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            lastCells = 0;
            Map map = Map;
            if (!SeaLevels.IsAnyLevel(map))
            {
                return;
            }
            Material mat = map.GetComponent<MapComponent_SeaSurface>()?.ShadowMaterial;
            Map sea = SeaLevels.IsSeaLevel(map) ? map : SeaLevels.SeaLevelOf(map);
            if (mat == null || sea == null)
            {
                return;
            }
            // 畫面上的一點看到的是海面座標 z + drop（和著色器一樣），燈光取那裡的
            float drop = SeaLevels.ViewDrop(map);
            CellRect cr = section.CellRect;
            // 格角的燈光每個角只算一次（相鄰格子共用）
            int cw = cr.Width + 1;
            Color32?[] corners = new Color32?[cw * (cr.Height + 1)];
            Color32 Corner(int x, int z)
            {
                int i = (z - cr.minZ) * cw + (x - cr.minX);
                return corners[i] ??= PlatformShadow.LampColor(sea, x, z + drop);
            }
            LayerSubMesh sm = GetSubMesh(mat);
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            foreach (IntVec3 c in cr)
            {
                if (!Covers(c, map))
                {
                    continue;
                }
                int start = sm.verts.Count;
                sm.verts.Add(new Vector3(c.x, y, c.z));
                sm.verts.Add(new Vector3(c.x, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z));
                sm.colors.Add(Corner(c.x, c.z));
                sm.colors.Add(Corner(c.x, c.z + 1));
                sm.colors.Add(Corner(c.x + 1, c.z + 1));
                sm.colors.Add(Corner(c.x + 1, c.z));
                sm.tris.Add(start);
                sm.tris.Add(start + 1);
                sm.tris.Add(start + 2);
                sm.tris.Add(start);
                sm.tris.Add(start + 2);
                sm.tris.Add(start + 3);
                lastCells++;
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}
