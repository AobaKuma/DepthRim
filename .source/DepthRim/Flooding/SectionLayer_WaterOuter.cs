using UnityEngine;
using Verse;
using RimWorld;

namespace DepthRim
{
    /// <summary>
    /// 外海層：畫在原版迷霧之上（著色器 renderQueue 3177、關閉深度測試，見 <c>DepthRim_WaterOuter</c>），
    /// 讓開放水域裡的一切看起來都泡在水裡，未探索的迷霧區變成深海；光照暗化（3180）在之後，燈光仍會照亮水面。
    ///
    /// <para><b>裁剪：</b>只輸出 <see cref="FloodingMath.OuterIncludes"/> 涵蓋的格子，密閉艙室和它們的牆不在網格裡，
    /// 所以艙室沿格子邊界乾淨地切開，交給 <see cref="SectionLayer_WaterVolume"/>（艙室層）畫裡面的積水。
    /// 網格本來就是逐格四邊形，不需要另外用模板遮罩，也不會和 Odyssey 的模板位元衝突。</para>
    ///
    /// <para><b>迷霧：</b>迷霧下的格子一律涵蓋、頂點只帶迷霧比例與光照 0，不用房間或地形資料，
    /// 避免洩漏未探索的洞穴與迷霧下有沒有光源。</para>
    ///
    /// <para><b>頂點色（實作指南 §0.3）：</b>r = 靠近艙室的程度（往艙室淡出）、g = 人造光（四格平均）、
    /// b = 迷霧比例、a = 看得到天空的比例（四格平均）。著色器的光照 = max(g, a × _SkyLight)。</para>
    ///
    /// <para><b>重建：</b><see cref="MapComponent_Flooding"/> 標記 <c>DepthRim_Water</c>；建築、屋頂、迷霧與
    /// 地面光照（<c>MapMeshFlagDefOf.GroundGlow</c>）改變也會重建。光源註冊與移除時原版會把整個照明範圍
    /// 標記 <c>GroundGlow</c>（<c>GlowGrid.RegisterGlower</c>／<c>DeRegisterGlower</c>），所以跨區塊的燈也涵蓋在內。</para>
    /// </summary>
    [StaticConstructorOnStartup]
    public class SectionLayer_WaterOuter : SectionLayer
    {
        // 每個頂點（左下、左上、右上、右下）共用的四格，相對於本格
        private static readonly IntVec3[][] CornerCells =
        {
            new[] { new IntVec3(-1, 0, -1), new IntVec3(0, 0, -1), new IntVec3(-1, 0, 0), IntVec3.Zero },
            new[] { new IntVec3(-1, 0, 0), IntVec3.Zero, new IntVec3(-1, 0, 1), new IntVec3(0, 0, 1) },
            new[] { IntVec3.Zero, new IntVec3(1, 0, 0), new IntVec3(0, 0, 1), new IntVec3(1, 0, 1) },
            new[] { new IntVec3(0, 0, -1), new IntVec3(1, 0, -1), IntVec3.Zero, new IntVec3(1, 0, 0) }
        };

        private static readonly Vector3[] CornerOffsets =
        {
            new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 1f), new Vector3(1f, 0f, 0f)
        };

        /// <summary>每個頂點的暫存人造光照值（最多四格）。</summary>
        private readonly float[] cornerLights = new float[4];

        public SectionLayer_WaterOuter(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)DepthRimDefOf.DepthRim_Water | (ulong)MapMeshFlagDefOf.Buildings
                                  | (ulong)MapMeshFlagDefOf.Roofs | (ulong)MapMeshFlagDefOf.FogOfWar
                                  | (ulong)MapMeshFlagDefOf.GroundGlow;
        }

        public override bool Visible => FloodingOverlay.Show;

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            MapComponent_Flooding flooding = Map.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return;
            }
            Map map = Map;
            LayerSubMesh sm = GetSubMesh(WaterTierMaterials.Outer(flooding.RenderTier));
            // 頂點只存人造光（g）與「看得到天空的比例」（a）；日光強度隨時間變，由 WaterTierMaterials.UpdateSkyLight 每幀設給 _SkyLight
            float y = AltitudeLayer.FogOfWar.AltitudeFor() + 0.01f;
            foreach (IntVec3 c in section.CellRect)
            {
                if (!Includes(c, map, flooding))
                {
                    continue;
                }
                bool cellFogged = c.Fogged(map);
                int start = sm.verts.Count;
                for (int corner = 0; corner < 4; corner++)
                {
                    int excluded = 0;
                    int fogged = 0;
                    int lightCells = 0;
                    float sky = 0f;
                    IntVec3[] cells = CornerCells[corner];
                    for (int i = 0; i < 4; i++)
                    {
                        IntVec3 n = c + cells[i];
                        if (!n.InBounds(map))
                        {
                            // 地圖外視為和本格相同（迷霧權重照舊，光照不計入）
                            if (cellFogged) { fogged++; }
                            continue;
                        }
                        if (n.Fogged(map))
                        {
                            // 迷霧格一律以 0 計入，不洩漏底下有沒有光源
                            fogged++;
                            cornerLights[lightCells++] = 0f;
                            continue;
                        }
                        if (!Includes(n, map, flooding))
                        {
                            excluded++;
                        }
                        cornerLights[lightCells++] = WaterLight.Artificial(map, n);
                        sky += WaterLight.SkyExposure(map, n);
                    }
                    byte light = cellFogged ? (byte)0 : FloodingMath.ToByte(FloodingMath.AverageLight(cornerLights, lightCells));
                    byte skyExposure = cellFogged || lightCells == 0 ? (byte)0 : FloodingMath.ToByte(sky / lightCells);
                    sm.verts.Add(new Vector3(c.x, y, c.z) + CornerOffsets[corner]);
                    sm.colors.Add(new Color32((byte)(excluded * 255 / 4), light, (byte)(fogged * 255 / 4), skyExposure));
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

        public static bool Includes(IntVec3 c, Map map, MapComponent_Flooding flooding)
        {
            bool fogged = c.Fogged(map);
            if (fogged)
            {
                return true;
            }
            bool solid = IsSolid(c, map);
            return FloodingMath.OuterIncludes(false, solid, flooding.IsOpenAt(c), solid && BordersCompartment(c, map, flooding));
        }

        /// <summary>八方向有已探索的密閉艙室格（不是實心、不是開放水域）。</summary>
        private static bool BordersCompartment(IntVec3 c, Map map, MapComponent_Flooding flooding)
        {
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[i];
                if (n.InBounds(map) && !n.Fogged(map) && !IsSolid(n, map) && !flooding.IsOpenAt(n))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>實心格：岩體、牆等填滿的建築，以及關著的門。</summary>
        public static bool IsSolid(IntVec3 c, Map map)
        {
            if (c.Filled(map))
            {
                return true;
            }
            Building_Door door = c.GetDoor(map);
            return door != null && !door.Open;
        }
    }
}
