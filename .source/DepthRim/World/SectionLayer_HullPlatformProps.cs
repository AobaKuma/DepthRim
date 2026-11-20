using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 船體平台的邊框與側面貼圖（掛在 TerrainDef 上），由 <see cref="SectionLayer_HullPlatformProps"/> 繪製。
    /// 貼圖路徑的意義和原版 <c>spaceEdgeGraphicData</c> 相同：邊框畫在平台格上，側面（高兩格）畫在平台南邊的深淵格上。
    /// </summary>
    public class HullPlatformPropsExtension : DefModExtension
    {
        public string oShapeTexPath;
        public string uShapeTexPath;
        public string cornerInnerTexPath;
        public string cornerOuterTexPath;
        public string flatTexPath;

        /// <summary>側面：一段的左端、右端、單獨一格，以及中段的幾種變化。</summary>
        public string sideLeftTexPath;
        public string sideRightTexPath;
        public string sideSingleTexPath;
        public List<string> sideLoopTexPaths = new List<string>();

        /// <summary>
        /// 海面地圖上浮在水面的平台（船體平台）：側面只畫這麼高（格）的乾舷，海面的影子與白沫貼著水線畫
        /// （<see cref="MapComponent_SeaSurface"/> 遮罩的 b）。0 = 不浮在水面（海上平台：高出海面，側面照常畫兩格）。
        /// </summary>
        public float seaSurfaceFreeboard;

        public bool FloatsOnSea => seaSurfaceFreeboard > 0f;

        [Unsaved(false)]
        private Dictionary<string, Material> materials;

        public Material MaterialFor(string texPath)
        {
            if (texPath.NullOrEmpty())
            {
                return null;
            }
            materials ??= new Dictionary<string, Material>();
            if (!materials.TryGetValue(texPath, out Material mat))
            {
                // 原版 Transparent（2900，貼圖 Clamp）：在深淵背景（2600）之後、水體（2950）與外海層（3177）之前
                mat = MaterialPool.MatFrom(texPath, ShaderDatabase.Transparent);
                materials[texPath] = mat;
            }
            return mat;
        }

        public IEnumerable<string> AllTexPaths()
        {
            yield return oShapeTexPath;
            yield return uShapeTexPath;
            yield return cornerInnerTexPath;
            yield return cornerOuterTexPath;
            yield return flatTexPath;
            yield return sideLeftTexPath;
            yield return sideRightTexPath;
            yield return sideSingleTexPath;
            foreach (string p in sideLoopTexPaths)
            {
                yield return p;
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            foreach (string p in AllTexPaths())
            {
                if (p.NullOrEmpty())
                {
                    yield return "a hull platform texture path is empty";
                }
            }
            if (sideLoopTexPaths.NullOrEmpty())
            {
                yield return "sideLoopTexPaths is empty";
            }
            if (seaSurfaceFreeboard < 0f || seaSurfaceFreeboard > 2f)
            {
                yield return "seaSurfaceFreeboard must be between 0 and 2 cells";
            }
        }
    }

    /// <summary>側面要用哪一段貼圖、畫多高：純函式，可單元測試。</summary>
    public static class HullPlatformProps
    {
        public enum SidePiece
        {
            Single,
            Left,
            Right,
            Loop
        }

        /// <summary>
        /// 一格平台南邊緣的側面用哪一段（比照原版 SectionLayer_TerrainEdges.DrawLoop）：
        /// 左右兩邊都還有平台就是中段，只有東邊有就是左端，只有西邊有就是右端，都沒有就是單獨一格。
        /// </summary>
        public static SidePiece PieceFor(bool platformWest, bool platformEast)
        {
            if (platformWest && platformEast)
            {
                return SidePiece.Loop;
            }
            if (platformEast)
            {
                return SidePiece.Left;
            }
            if (platformWest)
            {
                return SidePiece.Right;
            }
            return SidePiece.Single;
        }

        /// <summary>
        /// 側面貼圖高兩格，從平台南邊那格往下畫。第二格不是深淵（海床、岩體、另一塊平台）時只畫上半，
        /// 不能蓋在實地上——這正是改用自己的繪製層、而不是原版 TerrainEdges 的原因。
        /// 回傳要畫幾格高（0 = 南邊那格本身就不是深淵，不畫）。
        /// </summary>
        public static int SideCells(bool firstBelowOpen, bool secondBelowOpen)
        {
            if (!firstBelowOpen)
            {
                return 0;
            }
            return secondBelowOpen ? 2 : 1;
        }

        /// <summary>
        /// 側面要畫多高（格）：海面地圖上浮在水面的平台只畫乾舷（<paramref name="freeboard"/>），其他照 <see cref="SideCells"/>。
        /// 坐在海床上的平台（<paramref name="onSeabed"/>：鋪在海床上，或下錨停在海床上）南邊緊鄰海床時，
        /// 和海面一樣只露出乾舷，畫在海床格上；南邊是深淵時照常往下垂。
        /// 側面貼圖高兩格、頂端貼著平台，所以畫 h 格時取貼圖上方 h/2（v 從 1 − h/2 到 1）。
        /// </summary>
        public static float SideHeight(bool firstBelowOpen, bool secondBelowOpen, bool onSeaSurface, float freeboard, bool onSeabed = false)
        {
            if (!firstBelowOpen)
            {
                return onSeabed && !onSeaSurface && freeboard > 0f ? Mathf.Min(freeboard, 2f) : 0f;
            }
            if (onSeaSurface && freeboard > 0f)
            {
                return Mathf.Min(freeboard, 2f);
            }
            return SideCells(firstBelowOpen, secondBelowOpen);
        }

        public static float SideVMin(float height)
        {
            return 1f - Mathf.Clamp(height, 0f, 2f) / 2f;
        }
    }

    /// <summary>
    /// 船體平台的邊框與下方側面（比照 Odyssey 的 <c>SectionLayer_SubstructureProps</c>）。
    ///
    /// <para>不用原版 <c>spaceEdgeGraphicData</c>／<c>SectionLayer_TerrainEdges</c>：原版的側面高兩格、一律畫滿，
    /// 靠「側面先畫、地形後畫」把落在實地上的部分蓋掉；但深淵背景是不透明的整片四邊形，排序怎麼排都會有一邊出錯
    /// （側面被背景蓋掉，或側面浮在海床上）。這一層自己決定畫在哪：邊框只畫在平台格上，側面只畫進深淵格，
    /// 第二格不是深淵時只畫上半。材質用原版 Transparent（2900），在深淵背景之後、水體之前。</para>
    /// </summary>
    public class SectionLayer_HullPlatformProps : SectionLayer
    {
        [Flags]
        private enum Dirs
        {
            None = 0,
            North = 1,
            East = 2,
            South = 4,
            West = 8
        }

        private static readonly Vector2[] UVs =
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f)
        };

        private enum Border
        {
            OShape,
            UShape,
            CornerOuter,
            Flat
        }

        // 和原版 SectionLayer_TerrainEdges 相同的旋轉（貼圖本來就是照那一套畫的）
        private static readonly Dictionary<Dirs, (Border, Rot4)[]> EdgePieces = new Dictionary<Dirs, (Border, Rot4)[]>
        {
            { Dirs.North, new[] { (Border.Flat, Rot4.South) } },
            { Dirs.East, new[] { (Border.Flat, Rot4.West) } },
            { Dirs.South, new[] { (Border.Flat, Rot4.North) } },
            { Dirs.West, new[] { (Border.Flat, Rot4.East) } },
            { Dirs.North | Dirs.East, new[] { (Border.CornerOuter, Rot4.West) } },
            { Dirs.East | Dirs.South, new[] { (Border.CornerOuter, Rot4.North) } },
            { Dirs.South | Dirs.West, new[] { (Border.CornerOuter, Rot4.East) } },
            { Dirs.North | Dirs.West, new[] { (Border.CornerOuter, Rot4.South) } },
            { Dirs.North | Dirs.South, new[] { (Border.Flat, Rot4.South), (Border.Flat, Rot4.North) } },
            { Dirs.East | Dirs.West, new[] { (Border.Flat, Rot4.West), (Border.Flat, Rot4.East) } },
            { Dirs.North | Dirs.East | Dirs.South, new[] { (Border.UShape, Rot4.West) } },
            { Dirs.East | Dirs.South | Dirs.West, new[] { (Border.UShape, Rot4.North) } },
            { Dirs.North | Dirs.South | Dirs.West, new[] { (Border.UShape, Rot4.East) } },
            { Dirs.North | Dirs.East | Dirs.West, new[] { (Border.UShape, Rot4.South) } },
            { Dirs.North | Dirs.East | Dirs.South | Dirs.West, new[] { (Border.OShape, Rot4.North) } },
        };

        public SectionLayer_HullPlatformProps(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible => DebugViewSettings.drawTerrain;

        /// <summary>最近一次重建輸出的側面格數（給驗收測試）：每一項是（側面所在的格、畫幾格高）。</summary>
        public readonly List<(IntVec3 cell, float height)> lastSides = new List<(IntVec3, float)>();

        public static HullPlatformPropsExtension PropsAt(IntVec3 c, Map map)
        {
            if (!c.InBounds(map))
            {
                return null;
            }
            return map.terrainGrid.BaseTerrainAt(c)?.GetModExtension<HullPlatformPropsExtension>();
        }

        /// <summary>
        /// 平台格 <paramref name="c"/> 坐在海床上、南邊 <paramref name="below"/> 是露出來的海床：
        /// 平台底下原本是海床（<see cref="MapComponent_SeabedMemory"/> 有記錄：鋪在海床上或下錨停在海床上），
        /// 南邊那格在地圖內、不是深淵、也不是另一塊平台。
        /// </summary>
        public static bool RestsOnSeabed(IntVec3 c, IntVec3 below, Map map)
        {
            if (!below.InBounds(map) || IsOpen(below, map) || PropsAt(below, map) != null)
            {
                return false;
            }
            MapComponent_SeabedMemory memory = map.GetComponent<MapComponent_SeabedMemory>();
            return memory != null && memory.TryGetUnder(c, out _);
        }

        private static bool IsOpen(IntVec3 c, Map map)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            TerrainDef t = map.terrainGrid.TerrainAt(c);
            return t == null || t.dontRender;
        }

        /// <summary>原版地圖上的水域地形（海水、淡水、沼澤水都算；不是另一塊平台）。</summary>
        public static bool IsVanillaWater(IntVec3 c, Map map)
        {
            if (!c.InBounds(map) || PropsAt(c, map) != null)
            {
                return false;
            }
            TerrainDef t = map.terrainGrid.TerrainAt(c);
            return t != null && t.IsWater;
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            lastSides.Clear();
            Map map = Map;
            float borderAltitude = AltitudeLayer.TerrainScatter.AltitudeFor();
            float sideAltitude = AltitudeLayer.TerrainEdges.AltitudeFor();
            foreach (IntVec3 c in section.CellRect)
            {
                HullPlatformPropsExtension props = PropsAt(c, map);
                if (props == null)
                {
                    continue;
                }
                Dirs edges = Dirs.None;
                for (int i = 0; i < 4; i++)
                {
                    if (PropsAt(c + GenAdj.CardinalDirections[i], map) == null)
                    {
                        edges |= (Dirs)(1 << i);
                    }
                }
                Dirs corners = Dirs.None; // 用同樣的位元表示斜角：1 = 西南、2 = 西北、4 = 東北、8 = 東南
                for (int i = 0; i < 4; i++)
                {
                    if (PropsAt(c + GenAdj.DiagonalDirections[i], map) == null)
                    {
                        corners |= (Dirs)(1 << i);
                    }
                }
                DrawBorders(props, c, edges, corners, borderAltitude);
                if (edges.HasFlag(Dirs.South))
                {
                    DrawSide(props, c, map, sideAltitude);
                }
            }
            FinalizeMesh(MeshParts.All);
        }

        private void DrawBorders(HullPlatformPropsExtension props, IntVec3 c, Dirs edges, Dirs corners, float altitude)
        {
            if (EdgePieces.TryGetValue(edges, out (Border, Rot4)[] pieces))
            {
                foreach ((Border border, Rot4 rot) in pieces)
                {
                    AddQuad(props.MaterialFor(BorderPath(props, border)), c.x, c.z, 1f, 0f, 1f, altitude, rot);
                }
            }
            // 內角（比照原版：斜角空、兩邊不是邊緣）；西南 1、西北 2、東北 4、東南 8
            if (corners.HasFlag((Dirs)2) && !edges.HasFlag(Dirs.North) && !edges.HasFlag(Dirs.West))
            {
                AddQuad(props.MaterialFor(props.cornerInnerTexPath), c.x, c.z, 1f, 0f, 1f, altitude, Rot4.East);
            }
            if (corners.HasFlag((Dirs)4) && !edges.HasFlag(Dirs.North) && !edges.HasFlag(Dirs.East))
            {
                AddQuad(props.MaterialFor(props.cornerInnerTexPath), c.x, c.z, 1f, 0f, 1f, altitude, Rot4.South);
            }
            if (corners.HasFlag((Dirs)8) && !edges.HasFlag(Dirs.South) && !edges.HasFlag(Dirs.East))
            {
                AddQuad(props.MaterialFor(props.cornerInnerTexPath), c.x, c.z, 1f, 0f, 1f, altitude, Rot4.West);
            }
            if (corners.HasFlag((Dirs)1) && !edges.HasFlag(Dirs.South) && !edges.HasFlag(Dirs.West))
            {
                AddQuad(props.MaterialFor(props.cornerInnerTexPath), c.x, c.z, 1f, 0f, 1f, altitude, Rot4.North);
            }
        }

        private static string BorderPath(HullPlatformPropsExtension props, Border border)
        {
            switch (border)
            {
                case Border.OShape: return props.oShapeTexPath;
                case Border.UShape: return props.uShapeTexPath;
                case Border.CornerOuter: return props.cornerOuterTexPath;
                default: return props.flatTexPath;
            }
        }

        /// <summary>平台格 <paramref name="c"/> 的南邊緣往下畫側面（只畫進深淵格）。</summary>
        private void DrawSide(HullPlatformPropsExtension props, IntVec3 c, Map map, float altitude)
        {
            IntVec3 below = c + IntVec3.South;
            bool firstOpen = IsOpen(below, map);
            bool onSea = SeaSurface.IsSeaSurfaceMap(map);
            bool onSeabed;
            if (SeaLevels.IsVanillaSurface(map))
            {
                // 原版海岸地圖：南邊是原版水域就和海面一樣浮著、只露出乾舷；貼著陸地（岸、碼頭）就不畫
                firstOpen = IsVanillaWater(below, map);
                onSea = true;
                onSeabed = false;
            }
            else
            {
                onSeabed = RestsOnSeabed(c, below, map);
            }
            float cells = HullPlatformProps.SideHeight(firstOpen, IsOpen(below + IntVec3.South, map), onSea, props.seaSurfaceFreeboard, onSeabed);
            if (cells <= 0f)
            {
                return;
            }
            // 比照原版：左右鄰格是平台就算「接續」（L 形內角處側面直接頂到另一塊平台，不畫端蓋）
            bool west = PropsAt(c + IntVec3.West, map) != null;
            bool east = PropsAt(c + IntVec3.East, map) != null;
            string path;
            switch (HullPlatformProps.PieceFor(west, east))
            {
                case HullPlatformProps.SidePiece.Left: path = props.sideLeftTexPath; break;
                case HullPlatformProps.SidePiece.Right: path = props.sideRightTexPath; break;
                case HullPlatformProps.SidePiece.Loop: path = props.sideLoopTexPaths[Mathf.Abs(c.GetHashCode()) % props.sideLoopTexPaths.Count]; break;
                default: path = props.sideSingleTexPath; break;
            }
            // 貼圖高兩格、頂端貼著平台：畫兩格時用整張，只畫一格時只取上半（v 0.5..1），乾舷依高度取上方一段
            AddQuad(props.MaterialFor(path), c.x, c.z - cells, cells, HullPlatformProps.SideVMin(cells), 1f, altitude, Rot4.North);
            lastSides.Add((below, cells));
        }

        /// <summary>
        /// 一個四邊形：左下角 (x, z)、寬 1、高 <paramref name="height"/> 格，貼圖取 v 從 <paramref name="vMin"/> 到 <paramref name="vMax"/>；
        /// 旋轉比照原版（UV 依 Rot4 輪換）。
        /// </summary>
        private void AddQuad(Material mat, float x, float z, float height, float vMin, float vMax, float altitude, Rot4 rotation)
        {
            if (mat == null)
            {
                return;
            }
            LayerSubMesh sm = GetSubMesh(mat);
            int count = sm.verts.Count;
            int shift = Mathf.Abs(4 - rotation.AsInt);
            for (int i = 0; i < 4; i++)
            {
                sm.verts.Add(new Vector3(x + UVs[i].x, altitude, z + UVs[i].y * height));
                Vector2 uv = UVs[(shift + i) % 4];
                sm.uvs.Add(new Vector3(uv.x, Mathf.Lerp(vMin, vMax, uv.y), 0f));
            }
            sm.tris.Add(count);
            sm.tris.Add(count + 1);
            sm.tris.Add(count + 2);
            sm.tris.Add(count);
            sm.tris.Add(count + 2);
            sm.tris.Add(count + 3);
        }
    }
}
