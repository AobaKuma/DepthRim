using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 艙室層（設計稿 §2.2 視覺）：只畫密閉艙室裡的積水。每個有水的艙室格一個四邊形，四個頂點各自算水位、
    /// 岸邊程度與光照，交給 <c>DepthRim_WaterVolume</c> 著色器畫出波紋、光紋與柔和的邊緣。
    /// 開放水域與迷霧區由畫在迷霧之上的外海層（<see cref="SectionLayer_WaterOuter"/>）負責。
    ///
    /// <para><b>頂點色（實作指南 §0.3）：</b>a = 水位、r = 岸邊程度、g = 光照值（四格平均亮度）、b = 保留。
    /// 頂點水位只平均「同一片水」的格子（<see cref="FloodingMath.WaterCorner"/>），所以乾的艙室不會被隔壁的水染色；
    /// 碰到岩體、牆、關著的門就算岸邊。</para>
    ///
    /// <para><b>高度：</b>畫在 MoteLow 高度（地板、汙物之上，建築之下）。著色器開深度測試、不寫深度，
    /// 所以岩體與牆的邊緣會自然蓋住水，Pawn 與物品也畫在水上面（Pawn 的水線由 <see cref="PawnWaterRendering"/> 處理）。
    /// 迷霧畫在更上層，不需要另外處理。</para>
    ///
    /// <para><b>重建：</b>水位改變時 <see cref="MapComponent_Flooding"/> 標記 <c>DepthRim_Water</c>；
    /// 建築與屋頂改變（房間、岸邊會跟著變）以及地面光照改變（<c>MapMeshFlagDefOf.GroundGlow</c>）也會重建。
    /// 動畫由著色器依 _GameSeconds 計算，不需要重建。</para>
    /// </summary>
    [StaticConstructorOnStartup]
    public class SectionLayer_WaterVolume : SectionLayer
    {
        private static readonly IntVec3[][] CornerCells =
        {
            // 每個頂點（左下、左上、右上、右下）共用的四格，相對於本格
            new[] { new IntVec3(-1, 0, -1), new IntVec3(0, 0, -1), new IntVec3(-1, 0, 0), IntVec3.Zero },
            new[] { new IntVec3(-1, 0, 0), IntVec3.Zero, new IntVec3(-1, 0, 1), new IntVec3(0, 0, 1) },
            new[] { IntVec3.Zero, new IntVec3(1, 0, 0), new IntVec3(0, 0, 1), new IntVec3(1, 0, 1) },
            new[] { new IntVec3(0, 0, -1), new IntVec3(1, 0, -1), IntVec3.Zero, new IntVec3(1, 0, 0) }
        };

        private static readonly Vector3[] CornerOffsets =
        {
            new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 1f), new Vector3(1f, 0f, 0f)
        };

        /// <summary>
        /// 原版牆在格子南側露出的正面高度（佔一格的比例，依 1.7 牆貼圖量測）。北牆的這一段畫一條隨水位升高的水帶，
        /// 看起來像水貼著艙壁漲上來（使用者回報「水線能不能顯示在牆面上」）。
        /// </summary>
        public const float WallFaceDepth = 0.45f;

        /// <summary>牆面水帶頂點的 b 通道：頂邊（水線）與底邊。艙室水面的 b 一律是 0，著色器靠這個分辨兩種四邊形。</summary>
        public const byte FaceTopB = 128;

        public const byte FaceBottomB = 255;

        /// <summary>
        /// 水面往相鄰牆格延伸的距離（格）。牆的連接貼圖兩側有透明邊（ShipHull 的直牆約 0.1–0.15 格），
        /// 底下的地板會從縫裡露出來（使用者回報「左右兩邊的縫隙」）。艙室著色器有深度測試、牆比水高，
        /// 所以延伸進牆格的水只會從透明邊露出來，不會蓋到牆本身；延伸半格就不會從牆的另一側透出去。
        /// </summary>
        public const float WallOverlap = 0.5f;

        private readonly Color32[] cornerColors = new Color32[4];

        private readonly FloodingMath.CornerCell[] kinds = new FloodingMath.CornerCell[4];

        private readonly float[] cornerLevels = new float[4];

        private readonly float[] cornerLights = new float[4];

        public SectionLayer_WaterVolume(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)DepthRimDefOf.DepthRim_Water | (ulong)MapMeshFlagDefOf.Buildings
                                  | (ulong)MapMeshFlagDefOf.Roofs | (ulong)MapMeshFlagDefOf.GroundGlow;
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
            LayerSubMesh sm = GetSubMesh(WaterTierMaterials.Volume(flooding.RenderTier));
            float y = AltitudeLayer.MoteLow.AltitudeFor();
            CellRect rect = section.CellRect;
            foreach (IntVec3 c in rect)
            {
                float level = flooding.LevelAt(c);
                if (level <= 0.002f || flooding.IsOpenAt(c) || IsSolid(c, map))
                {
                    continue;
                }
                Room room = c.GetRoom(map);
                if (room == null)
                {
                    continue;
                }
                bool cellFogged = c.Fogged(map);
                int start = sm.verts.Count;
                for (int corner = 0; corner < 4; corner++)
                {
                    IntVec3[] cells = CornerCells[corner];
                    int lightCells = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        IntVec3 n = c + cells[i];
                        kinds[i] = Classify(n, room, map, out cornerLevels[i], flooding);
                        if (!n.InBounds(map))
                        {
                            continue;
                        }
                        // 迷霧格一律以 0 計入，不洩漏底下有沒有光源
                        cornerLights[lightCells++] = n.Fogged(map) ? 0f : WaterLight.Artificial(map, n); // 艙室有屋頂，只算人造光
                    }
                    FloodingMath.WaterCorner(kinds, cornerLevels, out float cornerLevel, out float shore);
                    byte light = cellFogged ? (byte)0 : FloodingMath.ToByte(FloodingMath.AverageLight(cornerLights, lightCells));
                    sm.verts.Add(new Vector3(c.x, y, c.z) + CornerOffsets[corner]);
                    cornerColors[corner] = new Color32(FloodingMath.ToByte(shore), light, 0, FloodingMath.ToByte(cornerLevel));
                    sm.colors.Add(cornerColors[corner]);
                }
                sm.tris.Add(start);
                sm.tris.Add(start + 1);
                sm.tris.Add(start + 2);
                sm.tris.Add(start);
                sm.tris.Add(start + 2);
                sm.tris.Add(start + 3);

                // 室內建築：水位高過建築（fillPercent）就在建築上面再蓋一層水（建築比水面高，平常會擋住水）
                if (!cellFogged && SubmergedBuildingAt(c, level, map))
                {
                    AddSubmergedOverlay(sm, c);
                }

                // 牆縫：往四個方向的實心格延伸半格，填滿牆貼圖兩側的透明邊（岸邊淡出設成 0，水要一路貼到牆）
                if (!cellFogged)
                {
                    AddWallOverlaps(sm, c, y, map);
                }

                // 北邊是牆：在牆的正面（牆格的南段）畫水帶，高度 = 正面高度 × 水位
                IntVec3 north = c + IntVec3.North;
                if (!cellFogged && north.InBounds(map) && north.Filled(map) && !north.Fogged(map))
                {
                    AddWallFace(sm, c, north, level, map);
                }
            }
            FinalizeMesh(MeshParts.All);
        }

        /// <summary>
        /// 這一格有沒有「比水面畫得高、而且已經被淹沒」的建築：牆與關著的門不算（另外處理）；
        /// 高度用 <see cref="ThingDef.fillPercent"/>。只看 Building 以上的繪製層，地板、電線本來就在水面下。
        /// </summary>
        public static bool SubmergedBuildingAt(IntVec3 c, float level, Map map)
        {
            List<Thing> things = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.def.category != ThingCategory.Building || t.def.altitudeLayer < AltitudeLayer.Building || t.def.Fillage == FillCategory.Full)
                {
                    continue;
                }
                if (FloodingMath.BuildingSubmerged(t.def.fillPercent, level))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>淹沒的建築上面再蓋一層同樣的水（顏色沿用本格四角，岸邊歸零），高度在建築之上、牆面水帶之下、Pawn 之下。</summary>
        private void AddSubmergedOverlay(LayerSubMesh sm, IntVec3 c)
        {
            float y = AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.005f;
            AddStrip(sm, new Vector3(c.x, y, c.z), new Vector3(c.x, y, c.z + 1), new Vector3(c.x + 1, y, c.z + 1), new Vector3(c.x + 1, y, c.z), 0, 1, 2, 3);
        }

        private void AddWallOverlaps(LayerSubMesh sm, IntVec3 c, float y, Map map)
        {
            // 角落索引：0 左下、1 左上、2 右上、3 右下
            if (IsSolidAt(c + IntVec3.West, map)) { AddStrip(sm, new Vector3(c.x - WallOverlap, y, c.z), new Vector3(c.x - WallOverlap, y, c.z + 1), new Vector3(c.x, y, c.z + 1), new Vector3(c.x, y, c.z), 0, 1, 1, 0); }
            if (IsSolidAt(c + IntVec3.East, map)) { AddStrip(sm, new Vector3(c.x + 1, y, c.z), new Vector3(c.x + 1, y, c.z + 1), new Vector3(c.x + 1 + WallOverlap, y, c.z + 1), new Vector3(c.x + 1 + WallOverlap, y, c.z), 3, 2, 2, 3); }
            if (IsSolidAt(c + IntVec3.South, map)) { AddStrip(sm, new Vector3(c.x, y, c.z - WallOverlap), new Vector3(c.x, y, c.z), new Vector3(c.x + 1, y, c.z), new Vector3(c.x + 1, y, c.z - WallOverlap), 0, 0, 3, 3); }
            if (IsSolidAt(c + IntVec3.North, map)) { AddStrip(sm, new Vector3(c.x, y, c.z + 1), new Vector3(c.x, y, c.z + 1 + WallOverlap), new Vector3(c.x + 1, y, c.z + 1 + WallOverlap), new Vector3(c.x + 1, y, c.z + 1), 1, 1, 2, 2); }
        }

        private static bool IsSolidAt(IntVec3 n, Map map)
        {
            return n.InBounds(map) && !n.Fogged(map) && IsSolid(n, map);
        }

        /// <summary>延伸條：四個頂點用本格邊上兩個角的顏色（水位、光照），岸邊程度歸零。</summary>
        private void AddStrip(LayerSubMesh sm, Vector3 a, Vector3 b, Vector3 c2, Vector3 d, int ca, int cb, int cc, int cd)
        {
            int start = sm.verts.Count;
            sm.verts.Add(a);
            sm.verts.Add(b);
            sm.verts.Add(c2);
            sm.verts.Add(d);
            sm.colors.Add(NoShore(cornerColors[ca]));
            sm.colors.Add(NoShore(cornerColors[cb]));
            sm.colors.Add(NoShore(cornerColors[cc]));
            sm.colors.Add(NoShore(cornerColors[cd]));
            sm.tris.Add(start);
            sm.tris.Add(start + 1);
            sm.tris.Add(start + 2);
            sm.tris.Add(start);
            sm.tris.Add(start + 2);
            sm.tris.Add(start + 3);
        }

        private static Color32 NoShore(Color32 c)
        {
            return new Color32(0, c.g, 0, c.a);
        }

        private static void AddWallFace(LayerSubMesh sm, IntVec3 water, IntVec3 wall, float level, Map map)
        {
            // 艙室著色器開深度測試、牆（Building 高度）會擋住水面；水帶畫在牆上，所以高度要在牆之上（BuildingOnTop），
            // 仍低於 Pawn，站在牆邊的人不會被水帶蓋住
            float y = AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.01f;
            float top = wall.z + WallFaceDepth * Mathf.Clamp01(level);
            byte light = FloodingMath.ToByte(WaterLight.Artificial(map, water));
            byte a = FloodingMath.ToByte(level);
            int start = sm.verts.Count;
            sm.verts.Add(new Vector3(wall.x, y, wall.z));
            sm.verts.Add(new Vector3(wall.x, y, top));
            sm.verts.Add(new Vector3(wall.x + 1, y, top));
            sm.verts.Add(new Vector3(wall.x + 1, y, wall.z));
            sm.colors.Add(new Color32(0, light, FaceBottomB, a));
            sm.colors.Add(new Color32(0, light, FaceTopB, a));
            sm.colors.Add(new Color32(0, light, FaceTopB, a));
            sm.colors.Add(new Color32(0, light, FaceBottomB, a));
            sm.tris.Add(start);
            sm.tris.Add(start + 1);
            sm.tris.Add(start + 2);
            sm.tris.Add(start);
            sm.tris.Add(start + 2);
            sm.tris.Add(start + 3);
        }

        private FloodingMath.CornerCell Classify(IntVec3 n, Room room, Map map, out float level, MapComponent_Flooding flooding)
        {
            level = 0f;
            if (!n.InBounds(map))
            {
                return FloodingMath.CornerCell.Ignore;
            }
            if (IsSolid(n, map))
            {
                return FloodingMath.CornerCell.Solid;
            }
            Room other = n.GetRoom(map);
            if (other == null)
            {
                return FloodingMath.CornerCell.Ignore;
            }
            if (other == room || other.IsDoorway || room.IsDoorway)
            {
                level = flooding.LevelAt(n);
                return FloodingMath.CornerCell.SameWater;
            }
            return FloodingMath.CornerCell.Ignore;
        }

        /// <summary>水擋得住的格子：岩體、牆等填滿的建築，以及關著的門。</summary>
        private static bool IsSolid(IntVec3 c, Map map)
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
