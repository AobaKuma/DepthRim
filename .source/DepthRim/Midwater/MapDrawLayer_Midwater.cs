using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 地形下方的中層水域（比照原版 <c>MapDrawLayer_OrbitalDebris</c>）：從深淵底下經過的生物。
    ///
    /// <para><b>只在深淵格露出來</b>：生物是每隻一個四邊形，畫在外海層（renderQueue 3177）之後、
    /// 光照暗化（3180）之前——畫在外海層下面會被 85% 的水色完全蓋掉（驗收截圖實測）。
    /// 著色器用 <see cref="MapComponent_Midwater.RebuildMask"/> 的遮罩把四邊形裁在已探索的深淵格裡，
    /// 所以牠們只會在深淵上方出現一片暗影，海床、岩體與迷霧上都不會有。</para>
    ///
    /// <para><b>深度</b>：每種生物的 <see cref="MidwaterCreatureDef.parallax"/> 是牠在地板下多深，
    /// 位置與大小都往鏡頭正下方縮（<see cref="MidwaterMath.ProjectDepth"/>），鏡頭平移時牠們移動得比地板慢。</para>
    ///
    /// <para>原版用反射建立所有非 SectionLayer 的 <see cref="MapDrawLayer"/>（每張地圖都有一份），
    /// 所以建構子不能做重活；不是變溫層或深海的地圖 <see cref="Visible"/> 一律是 false。</para>
    /// </summary>
    public class MapDrawLayer_Midwater : MapDrawLayer
    {
        /// <summary>高度只影響排序以外的東西（例如視錐）；實際前後順序由著色器的 renderQueue 決定。</summary>
        public static float Altitude => AltitudeLayer.BelowTerrain.AltitudeFor() + 0.05f;

        private MapComponent_Midwater comp;

        private readonly Dictionary<MidwaterCreatureDef, LayerSubMesh> meshes = new Dictionary<MidwaterCreatureDef, LayerSubMesh>();

        private static readonly List<MidwaterCreature> tmpDraw = new List<MidwaterCreature>();

        /// <summary>最近一幀畫出了幾隻（給驗收測試）。</summary>
        public int LastDrawnCount { get; private set; }

        public MapDrawLayer_Midwater(Map map) : base(map)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.FogOfWar;
        }

        private MapComponent_Midwater Comp => comp ?? (comp = Map.GetComponent<MapComponent_Midwater>());

        public override bool Visible
        {
            get
            {
                MapComponent_Midwater c = Comp;
                return c != null && MidwaterMath.ShowsMidwater(c.RenderTier) && !GravshipCapturer.IsGravshipRenderInProgress;
            }
        }

        /// <summary>地形或迷霧改變：重建深淵遮罩。生物本身每幀重畫，不在這裡。</summary>
        public override void Regenerate()
        {
            Dirty = false;
            Comp?.RebuildMask();
        }

        public override void DrawLayer()
        {
            LastDrawnCount = 0;
            MapComponent_Midwater c = Comp;
            if (c == null || c.Count == 0 || !Visible)
            {
                return;
            }
            Texture2D mask = c.Mask ?? c.RebuildMask();
            Vector3 cam = Find.Camera.transform.position;
            Vector2 camXZ = new Vector2(cam.x, cam.z);
            Vector4 mapSize = new Vector4(Map.Size.x, Map.Size.z, 0f, 0f);

            tmpDraw.Clear();
            tmpDraw.AddRange(c.Ambient);
            tmpDraw.AddRange(c.Giants); // 巨物最後畫，蓋在小生物上面
            foreach (LayerSubMesh sm in meshes.Values)
            {
                sm.Clear(MeshParts.All);
            }
            float y = Altitude;
            for (int i = 0; i < tmpDraw.Count; i++)
            {
                MidwaterCreature creature = tmpDraw[i];
                MidwaterCreatureDef def = creature.def;
                float alpha = creature.Alpha;
                if (def == null || alpha <= 0.001f || def.Material == null)
                {
                    continue;
                }
                if (!meshes.TryGetValue(def, out LayerSubMesh sm))
                {
                    sm = new LayerSubMesh(new Mesh { name = "DepthRim_Midwater_" + def.defName }, def.Material);
                    meshes[def] = sm;
                    subMeshes.Add(sm); // 讓原版的 Dispose 一起銷毀網格
                }
                Vector2 pos = MidwaterMath.ProjectDepth(creature.Position, camXZ, def.parallax);
                Vector2 size = def.drawSize * (creature.scale * MidwaterMath.DepthScale(def.parallax));
                AddQuad(sm, pos, creature.Heading, size, y, alpha, creature.phase);
                LastDrawnCount++;
            }
            foreach (KeyValuePair<MidwaterCreatureDef, LayerSubMesh> kv in meshes)
            {
                LayerSubMesh sm = kv.Value;
                if (sm.verts.Count == 0)
                {
                    continue;
                }
                sm.FinalizeMesh(MeshParts.All);
                // 生物會游出地圖外，邊界框給大一點，避免被視錐裁掉（網格每幀重建，自動算的框不一定可靠）
                sm.mesh.bounds = GetBoundaryRect().ExpandedBy(80).ToBounds();
                Material mat = kv.Key.Material;
                mat.SetTexture("_MaskTex", mask);
                mat.SetVector("_MapSize", mapSize);
                Graphics.DrawMesh(sm.mesh, Matrix4x4.identity, mat, 0);
            }
        }

        /// <summary>
        /// 一隻生物的四邊形：中心 <paramref name="pos"/>、沿 <paramref name="heading"/> 旋轉。
        /// UV 的 u 沿著身體（0 = 尾、1 = 頭），頂點色 a = 淡入淡出，r = 擺動相位（0..1）。
        /// </summary>
        private static void AddQuad(LayerSubMesh sm, Vector2 pos, float heading, Vector2 size, float y, float alpha, float phase)
        {
            Vector2 fwd = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * (size.x * 0.5f);
            Vector2 side = new Vector2(-Mathf.Sin(heading), Mathf.Cos(heading)) * (size.y * 0.5f);
            int start = sm.verts.Count;
            Vector2 a = pos - fwd - side; // 尾、右
            Vector2 b = pos - fwd + side; // 尾、左
            Vector2 c = pos + fwd + side; // 頭、左
            Vector2 d = pos + fwd - side; // 頭、右
            sm.verts.Add(new Vector3(a.x, y, a.y));
            sm.verts.Add(new Vector3(b.x, y, b.y));
            sm.verts.Add(new Vector3(c.x, y, c.y));
            sm.verts.Add(new Vector3(d.x, y, d.y));
            sm.uvs.Add(new Vector3(0f, 0f, 0f));
            sm.uvs.Add(new Vector3(0f, 1f, 0f));
            sm.uvs.Add(new Vector3(1f, 1f, 0f));
            sm.uvs.Add(new Vector3(1f, 0f, 0f));
            Color32 col = new Color32((byte)(Mathf.Repeat(phase, 1f) * 255f), 0, 0, (byte)(Mathf.Clamp01(alpha) * 255f));
            sm.colors.Add(col);
            sm.colors.Add(col);
            sm.colors.Add(col);
            sm.colors.Add(col);
            sm.tris.Add(start);
            sm.tris.Add(start + 1);
            sm.tris.Add(start + 2);
            sm.tris.Add(start);
            sm.tris.Add(start + 2);
            sm.tris.Add(start + 3);
        }
    }
}
