using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 平台層往下看：把水面層的地形（浮在水上的船體平台）與建築畫在 <see cref="MapComponent_SeaSurface.SeaDropCells"/> 格下方。
    ///
    /// <para>不是即時的：只有水面層的地形或建築變了（<see cref="Patch_SeaLevelChanged"/>），平台層才在下一次檢查（每兩秒）時
    /// 用自訂旗標 <c>DepthRim_LowerLevel</c> 把整張地圖的這一層標記重建。Pawn 與物品不畫。</para>
    ///
    /// <para>做法：先照水面層的座標把地形四邊形與建築（原版 <see cref="Thing.Print"/>）印進這一層，最後把每個子網格往南移、
    /// 材質換成背景著色器版本（<see cref="BackgroundMaterial"/>，queue 1940，略暗）：畫在海面（1900）之上、支柱柱身（1950）之下、
    /// 平台層的甲板（2000）之下，被平台蓋住的地方自然看不到。</para>
    /// </summary>
    public class SectionLayer_LowerLevel : SectionLayer
    {
        /// <summary>遠在下方的東西略暗、略偏藍。</summary>
        public static readonly Color Haze = new Color(0.78f, 0.84f, 0.9f, 1f);

        private static readonly Dictionary<Material, Material> backgroundMats = new Dictionary<Material, Material>();

        private static readonly HashSet<Material> backgroundMatSet = new HashSet<Material>();

        private static Shader backgroundShader;

        /// <summary>給驗收測試：最近一次重建畫了幾格地形、幾個建築。</summary>
        public int lastCells;

        public int lastThings;

        public SectionLayer_LowerLevel(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)DepthRimDefOf.DepthRim_LowerLevel;
        }

        public override bool Visible => DebugViewSettings.drawTerrain;

        /// <summary>原材質的背景版本：同一張貼圖，背景著色器，乘上 <see cref="Haze"/>。</summary>
        public static Material BackgroundMaterial(Material source)
        {
            if (source == null)
            {
                return null;
            }
            if (backgroundMatSet.Contains(source))
            {
                return source;
            }
            if (backgroundMats.TryGetValue(source, out Material mat))
            {
                return mat;
            }
            backgroundShader ??= DefDatabase<ShaderTypeDef>.GetNamedSilentFail("DepthRim_BackgroundLower")?.Shader;
            if (backgroundShader == null)
            {
                return null;
            }
            mat = new Material(backgroundShader)
            {
                name = "DepthRim_Lower_" + source.name,
                mainTexture = source.mainTexture,
            };
            // 著色器的 @default 不會自動套用，每個參數都要設
            mat.SetColor("_Color", source.color * Haze);
            mat.SetVector("_MainTex_ST", new Vector4(0f, 0f, 1f, 1f));
            backgroundMats[source] = mat;
            backgroundMatSet.Add(mat);
            return mat;
        }

        /// <summary>水面層的格子 <paramref name="seaCell"/> 畫在平台層的哪一格（往南移 drop 格，取四邊形中心所在的格）。</summary>
        public static IntVec3 ScreenCell(IntVec3 seaCell, float drop)
        {
            return new IntVec3(seaCell.x, 0, Mathf.FloorToInt(seaCell.z + 0.5f - drop));
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            lastCells = 0;
            lastThings = 0;
            Map map = Map;
            Map sea = SeaLevels.SeaLevelOf(map);
            if (sea == null)
            {
                return;
            }
            float drop = SeaLevels.ViewDrop(map);
            CellRect cr = section.CellRect;
            // 畫進這個區塊的水面層格子：往北 drop 格的那一片（多抓一排，再用 ScreenCell 決定歸屬）
            CellRect window = CellRect.FromLimits(cr.minX, cr.minZ + Mathf.FloorToInt(drop), cr.maxX, cr.maxZ + Mathf.CeilToInt(drop)).ClipInsideMap(sea);
            float y = AltitudeLayer.Terrain.AltitudeFor();
            foreach (IntVec3 c in window)
            {
                if (!cr.Contains(ScreenCell(c, drop)))
                {
                    continue;
                }
                TerrainDef t = sea.terrainGrid.TerrainAt(c);
                if (t == null || t == DepthRimDefOf.DepthRim_OpenSea)
                {
                    continue;
                }
                AddQuad(t.DrawMatSingle, c.x, c.z, 1f, 0f, 1f, y);
                HullPlatformPropsExtension props = t.GetModExtension<HullPlatformPropsExtension>();
                if (props != null && props.FloatsOnSea && SeaSurface.IsOpenSea(c + IntVec3.South, sea))
                {
                    // 南緣的乾舷側面（和水面層看到的一樣高）
                    float h = props.seaSurfaceFreeboard;
                    AddQuad(props.MaterialFor(props.sideLoopTexPaths[0]), c.x, c.z - h, h, HullPlatformProps.SideVMin(h), 1f, y);
                }
                lastCells++;
            }
            foreach (Thing thing in sea.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
            {
                if (!thing.Spawned || thing is Building_PillarFoot || thing.def.drawerType == DrawerType.None || !cr.Contains(ScreenCell(thing.Position, drop)))
                {
                    continue;
                }
                try
                {
                    thing.Print(this);
                    lastThings++;
                }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[DepthRim] Could not print {thing} from the sea level onto the platform level: {e.Message}", thing.thingIDNumber ^ 0x10E7);
                }
            }
            // 整層往南移 drop 格，搬進背景版本材質的子網格；影子類材質不畫。
            // 印出來的子網格（原材質）清空後留在清單裡，下次重建時原版的 GetSubMesh 會重用，不會越來越多。
            int count = subMeshes.Count;
            for (int i = 0; i < count; i++)
            {
                LayerSubMesh src = subMeshes[i];
                if (src.verts.Count == 0 || backgroundMatSet.Contains(src.material))
                {
                    continue;
                }
                Material bg = src.material?.shader != null && src.material.shader.name.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) < 0
                    ? BackgroundMaterial(src.material)
                    : null;
                if (bg != null)
                {
                    LayerSubMesh dst = GetSubMesh(bg);
                    int start = dst.verts.Count;
                    bool colors = src.colors.Count == src.verts.Count;
                    for (int v = 0; v < src.verts.Count; v++)
                    {
                        Vector3 p = src.verts[v];
                        dst.verts.Add(new Vector3(p.x, y, p.z - drop));
                        dst.uvs.Add(v < src.uvs.Count ? src.uvs[v] : Vector3.zero);
                        dst.colors.Add(colors ? src.colors[v] : new Color32(255, 255, 255, 255));
                    }
                    for (int t = 0; t < src.tris.Count; t++)
                    {
                        dst.tris.Add(src.tris[t] + start);
                    }
                }
                src.Clear(MeshParts.All);
            }
            FinalizeMesh(MeshParts.All);
        }

        private void AddQuad(Material mat, float x, float z, float height, float vMin, float vMax, float y)
        {
            if (mat == null)
            {
                return;
            }
            LayerSubMesh sm = GetSubMesh(mat);
            int start = sm.verts.Count;
            sm.verts.Add(new Vector3(x, y, z));
            sm.verts.Add(new Vector3(x, y, z + height));
            sm.verts.Add(new Vector3(x + 1f, y, z + height));
            sm.verts.Add(new Vector3(x + 1f, y, z));
            sm.uvs.Add(new Vector3(0f, vMin, 0f));
            sm.uvs.Add(new Vector3(0f, vMax, 0f));
            sm.uvs.Add(new Vector3(1f, vMax, 0f));
            sm.uvs.Add(new Vector3(1f, vMin, 0f));
            sm.tris.Add(start);
            sm.tris.Add(start + 1);
            sm.tris.Add(start + 2);
            sm.tris.Add(start);
            sm.tris.Add(start + 2);
            sm.tris.Add(start + 3);
        }
    }
}
