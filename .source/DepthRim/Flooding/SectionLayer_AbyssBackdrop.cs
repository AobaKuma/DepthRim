using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 深淵背景層（二期 P2-10）：只在 <see cref="DepthRimDefOf.DepthRim_Abyss"/> 格輸出四邊形，
    /// 畫在地形之下（<c>AltitudeLayer.BelowTerrain</c>）。深淵地形本身設了 <c>dontRender</c>，
    /// 所以玩家看到的就是這一層：往下的黑暗、緩慢下沉的微粒，靠崖壁的那一圈才吃得到光。
    ///
    /// <para>頂點色：r = 離崖壁的權重（<see cref="FloodingMath.AbyssRimWeight"/>，4 格以外 0）、
    /// g = 光照（與水面同一套 <see cref="WaterLight.At"/>）。迷霧格不畫——迷霧由原版覆蓋，
    /// 深淵不該洩漏迷霧底下的資訊。</para>
    /// </summary>
    public class SectionLayer_AbyssBackdrop : SectionLayer
    {
        /// <summary>離崖壁幾格以內算「還看得到崖壁」。</summary>
        public const int RimCells = 4;

        /// <summary>
        /// 深淵幾乎吃不到日光：頂點光照只取天空光的這個比例（人造光不受影響，燈照得到崖邊）。
        /// 這裡**不能**傳 1.0——那會讓整個深淵的 g 都是天空光（0.925 → 236），
        /// 於是著色器沒有亮度差可以畫，無論 r 是 255 還是 0（P2-10 實測踩過）。
        /// </summary>
        public const float AmbientReach = 0.08f;

        private static Material material;

        private static ShaderTypeDef shaderDef;
        private static bool shaderLookupFailed;

        /// <summary>
        /// 深淵背景的著色器 Def。**不能**放進 <see cref="DepthRimDefOf"/>：`[DefOf]` 用欄位名稱當 defName，
        /// 而地形與著色器都叫 <c>DepthRim_Abyss</c>（型別不同、可以同名），兩個欄位會撞名。用型別查表。
        /// </summary>
        private static ShaderTypeDef ShaderDef
        {
            get
            {
                if (shaderDef == null && !shaderLookupFailed)
                {
                    shaderDef = DefDatabase<ShaderTypeDef>.GetNamedSilentFail("DepthRim_Abyss");
                    if (shaderDef == null)
                    {
                        shaderLookupFailed = true;
                        Log.ErrorOnce("[DepthRim] ShaderTypeDef DepthRim_Abyss is missing; the abyss backdrop will not be drawn.", 0x1A5B2);
                    }
                }
                return shaderDef;
            }
        }

        public SectionLayer_AbyssBackdrop(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain
                                  | (ulong)DepthRimDefOf.DepthRim_Water
                                  | (ulong)MapMeshFlagDefOf.FogOfWar;
        }

        public override bool Visible => true;

        /// <summary>每幀設定日光強度（深淵的日光穿透固定是 <see cref="AmbientReach"/>）。</summary>
        public static void UpdateSkyLight(Map map)
        {
            material?.SetFloat("_SkyLight", WaterLight.SkyLevel(map, AmbientReach));
        }

        /// <summary>給驗收測試：目前材質上的日光值（材質還沒建立時 -1）。</summary>
        public static float CurrentSkyLight => material != null ? material.GetFloat("_SkyLight") : -1f;

        private static Material Material
        {
            get
            {
                if (material == null)
                {
                    Shader shader = ShaderDef?.Shader;
                    if (shader == null)
                    {
                        return null;
                    }
                    material = new Material(shader);
                    material.name = "DepthRim_Abyss";
                    material.mainTexture = BaseContent.WhiteTex;
                    material.SetColor("_Color", new Color(0.012f, 0.020f, 0.038f));
                    material.SetColor("_DeepColor", new Color(0.002f, 0.004f, 0.010f));
                    material.SetColor("_RimColor", new Color(0.32f, 0.42f, 0.52f));
                    material.SetFloat("_ParticleDensity", 0.35f);
                    material.SetFloat("_ParticleSpeed", 0.5f);
                    material.SetFloat("_CameraZoom", 14f);
                }
                return material;
            }
        }

        /// <summary>除錯動作「重建水體材質」時一起重建（深淵材質也是這樣建出來的）。</summary>
        public static void Reset()
        {
            if (material != null)
            {
                Object.Destroy(material);
                material = null;
            }
        }

        /// <summary>某一格是不是深淵（界外一律不算）。</summary>
        private static bool IsAbyss(IntVec3 c, Map map)
        {
            return c.InBounds(map) && map.terrainGrid.TerrainAt(c) == DepthRimDefOf.DepthRim_Abyss;
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            int any = 0;
            foreach (IntVec3 c in section.CellRect)
            {
                if (IsAbyss(c, map) && !c.Fogged(map))
                {
                    any = 1;
                    break;
                }
            }
            if (any == 0)
            {
                return; // 這個區塊沒有（未探索的）深淵，什麼都不用畫
            }
            Material mat = Material;
            if (mat == null)
            {
                return; // 著色器沒解析到（上面已經報過錯），不要拿 null 去 GetSubMesh
            }
            LayerSubMesh sm = GetSubMesh(mat);
            float y = AltitudeLayer.BelowTerrain.AltitudeFor();
            foreach (IntVec3 c in section.CellRect)
            {
                if (!IsAbyss(c, map) || c.Fogged(map))
                {
                    continue;
                }
                int distance = FloodingMath.AbyssRimDistance(c, RimCells, n => IsAbyss(n, map));
                byte rim = FloodingMath.ToByte(FloodingMath.AbyssRimWeight(distance, RimCells));
                // 人造光烘進頂點；日光由 _SkyLight 每幀乘上（見 WaterLight.Artificial 的說明）
                byte light = FloodingMath.ToByte(WaterLight.Artificial(map, c));
                byte sky = FloodingMath.ToByte(WaterLight.SkyExposure(map, c));
                int start = sm.verts.Count;
                sm.verts.Add(new Vector3(c.x, y, c.z));
                sm.verts.Add(new Vector3(c.x, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z));
                Color32 color = new Color32(rim, light, 0, sky);
                sm.colors.Add(color);
                sm.colors.Add(color);
                sm.colors.Add(color);
                sm.colors.Add(color);
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
