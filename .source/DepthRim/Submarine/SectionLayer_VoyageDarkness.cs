using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 航行中潛艇地圖的艙外黑暗：船體以外的格子蓋上一層地圖界外的顏色，離船體越遠越不透明
    /// （<see cref="VoyageView.FadeAlpha"/>），所以艙外幾乎看不到、船體邊緣自然淡出到地圖邊界外的顏色。
    ///
    /// <para>著色器 <c>DepthRim_VoyageDarkness</c> 排在原版光照暗化（3180）之後、關閉深度測試：
    /// 界外色本身不受日夜與燈光影響，和地圖外面一樣。只在 <see cref="VoyageView.IsVoyageMap"/> 的地圖輸出網格。</para>
    ///
    /// <para>船體格：不是深淵的地形、有屋頂、或有建築。頂點 a = 四個相鄰格離船體距離平均後的覆蓋程度。</para>
    /// </summary>
    public class SectionLayer_VoyageDarkness : SectionLayer
    {
        private static readonly Vector3[] CornerOffsets =
        {
            new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 1f), new Vector3(1f, 0f, 0f)
        };

        // 每個頂點（左下、左上、右上、右下）共用的四格，相對於本格
        private static readonly IntVec3[][] CornerCells =
        {
            new[] { new IntVec3(-1, 0, -1), new IntVec3(0, 0, -1), new IntVec3(-1, 0, 0), IntVec3.Zero },
            new[] { new IntVec3(-1, 0, 0), IntVec3.Zero, new IntVec3(-1, 0, 1), new IntVec3(0, 0, 1) },
            new[] { IntVec3.Zero, new IntVec3(1, 0, 0), new IntVec3(0, 0, 1), new IntVec3(1, 0, 1) },
            new[] { new IntVec3(0, 0, -1), new IntVec3(1, 0, -1), IntVec3.Zero, new IntVec3(1, 0, 0) }
        };

        private static Material material;
        private static ShaderTypeDef shaderDef;
        private static bool shaderLookupFailed;

        // 同一幀重建的所有區塊共用一份距離場（整張地圖的倒角距離，幾毫秒）
        private static Map cachedMap;
        private static int cachedFrame = -1;
        private static float[] cachedField;

        public SectionLayer_VoyageDarkness(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Roofs;
        }

        public override bool Visible => true;

        private static Material Material
        {
            get
            {
                if (material == null)
                {
                    if (shaderDef == null && !shaderLookupFailed)
                    {
                        shaderDef = DefDatabase<ShaderTypeDef>.GetNamedSilentFail("DepthRim_VoyageDarkness");
                        if (shaderDef == null)
                        {
                            shaderLookupFailed = true;
                            Log.ErrorOnce("[DepthRim] ShaderTypeDef DepthRim_VoyageDarkness is missing; the voyage darkness will not be drawn.", 0x1A5B3);
                        }
                    }
                    Shader shader = shaderDef?.Shader;
                    if (shader == null)
                    {
                        return null;
                    }
                    material = new Material(shader);
                    material.name = "DepthRim_VoyageDarkness";
                    material.mainTexture = BaseContent.WhiteTex;
                    material.SetColor("_Color", VoyageView.OutOfBoundsColor);
                    material.SetColor("_SpeckColor", new Color(0.55f, 0.62f, 0.70f));
                    material.SetFloat("_SpeckStrength", 0.35f);
                }
                return material;
            }
        }

        /// <summary>每幀：更新艙外微粒的流動（<see cref="VoyageMotion"/>）。</summary>
        public static void UpdateFrame(Map map)
        {
            if (VoyageView.IsVoyageMap(map))
            {
                VoyageMotion.Update(map, material);
            }
        }

        /// <summary>除錯動作「重建水體材質」時一起重建。</summary>
        public static void Reset()
        {
            if (material != null)
            {
                Object.Destroy(material);
                material = null;
            }
        }

        /// <summary>船體格：不是深淵的地形、有屋頂、或有建築。</summary>
        public static bool IsHull(IntVec3 c, Map map)
        {
            return map.terrainGrid.TerrainAt(c) != DepthRimDefOf.DepthRim_Abyss
                   || map.roofGrid.Roofed(c)
                   || c.GetEdifice(map) != null;
        }

        /// <summary>整張地圖每格離船體的距離（格，上限 <see cref="VoyageView.FadeCells"/>）。</summary>
        public static float[] FieldFor(Map map)
        {
            int frame = Time.frameCount;
            if (cachedMap == map && cachedFrame == frame && cachedField != null)
            {
                return cachedField;
            }
            IntVec3 size = map.Size;
            cachedField = VoyageView.DistanceField(size.x, size.z, (x, z) => IsHull(new IntVec3(x, 0, z), map), VoyageView.FadeCells);
            cachedMap = map;
            cachedFrame = frame;
            return cachedField;
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            if (!VoyageView.IsVoyageMap(map))
            {
                return;
            }
            Material mat = Material;
            if (mat == null)
            {
                return;
            }
            float[] field = FieldFor(map);
            int width = map.Size.x;
            LayerSubMesh sm = GetSubMesh(mat);
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            foreach (IntVec3 c in section.CellRect)
            {
                if (field[c.z * width + c.x] <= 0f)
                {
                    continue; // 船體本身不蓋
                }
                int start = sm.verts.Count;
                for (int corner = 0; corner < 4; corner++)
                {
                    float sum = 0f;
                    int n = 0;
                    IntVec3[] cells = CornerCells[corner];
                    for (int i = 0; i < 4; i++)
                    {
                        IntVec3 a = c + cells[i];
                        if (!a.InBounds(map))
                        {
                            continue;
                        }
                        sum += field[a.z * width + a.x];
                        n++;
                    }
                    float alpha = VoyageView.FadeAlpha(n > 0 ? sum / n : VoyageView.FadeCells);
                    sm.verts.Add(new Vector3(c.x, y, c.z) + CornerOffsets[corner]);
                    sm.colors.Add(new Color32(255, 255, 255, FloodingMath.ToByte(alpha)));
                }
                sm.tris.Add(start);
                sm.tris.Add(start + 1);
                sm.tris.Add(start + 2);
                sm.tris.Add(start);
                sm.tris.Add(start + 2);
                sm.tris.Add(start + 3);
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}
