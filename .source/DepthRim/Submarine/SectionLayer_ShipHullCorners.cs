using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>標記用 <see cref="SectionLayer_ShipHullCorners"/> 補斜角的牆（目前是抗壓水密牆）。</summary>
    public class ShipHullAngledExtension : DefModExtension
    {
        /// <summary>斜角貼圖路徑前綴，後面接 northwest、Partial_northwest… 等。原版重力船的是 Things/Building/Linked/GravshipHull/AngledGravshipHull_。</summary>
        public string texPathPrefix = "Thing/ShipHullAngled/ShipHullAngled_";

        /// <summary>這套貼圖有沒有 _Linked/_Diag 變體（原版重力船的沒有）。</summary>
        public bool lowerVariants = true;
    }

    [StaticConstructorOnStartup]
    public class SectionLayer_ShipHullCorners : SectionLayer
    {
        private enum CornerType
        {
            None,
            Corner_NW,
            Corner_NE,
            Corner_SW,
            Corner_SE,
            Diagonal_NW,
            Diagonal_NE,
            Diagonal_SW,
            Diagonal_SE
        }

        private enum Lower
        {
            None,
            Linked,
            Diag
        }

        private static readonly Vector2[] UVs =
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f)
        };

        private const float CornerScale = 2f;

        private const string TexPathPrefix = "Thing/ShipHullAngled/ShipHullAngled_";

        private static readonly string[] TexNames =
        {
            null,
            "northwest",
            "northeast",
            "southwest",
            "southeast",
            "Partial_northwest",
            "Partial_northeast",
            "Partial_southwest",
            "Partial_southeast"
        };

        private static readonly string[] LowerSuffix = { "", "_Linked", "_Diag" };

        // [CornerType, Lower]；只有 NW/NE 類有 _Linked/_Diag，其他類一律用 None
        private static readonly Material[,] mats = LoadMaterials();

        private static readonly float CornerAltitude = AltitudeLayer.BuildingOnTop.AltitudeFor();

        // 與原版相同的順序：N, E, S, W, NW, NE, SE, SW
        private static readonly IntVec3[] Directions =
        {
            IntVec3.North,
            IntVec3.East,
            IntVec3.South,
            IntVec3.West,
            IntVec3.North + IntVec3.West,
            IntVec3.North + IntVec3.East,
            IntVec3.South + IntVec3.East,
            IntVec3.South + IntVec3.West
        };

        private static readonly int[] ColorSourceOrder = { 0, 2, 1, 3 };

        private static readonly bool[] tmpChecks = new bool[Directions.Length];

        public SectionLayer_ShipHullCorners(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Things | (ulong)MapMeshFlagDefOf.Roofs;
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            foreach (IntVec3 cell in section.CellRect)
            {
                if (ShouldDrawCornerPiece(cell, map, out CornerType cornerType, out Color color))
                {
                    Material mat = mats[(int)cornerType, (int)LowerOf(cell, cornerType, map)];
                    AddQuad(mat, cell + GetOffset(cornerType), color, IsCornerIndoorMasked(cell, cornerType, map));
                }
            }
            FinalizeMesh(MeshParts.All);
        }

        private static Material[,] LoadMaterials()
        {
            // 目前只有一種牆用斜角：取第一個掛了擴充的 def 的設定
            ShipHullAngledExtension ext = null;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                ext = def.GetModExtension<ShipHullAngledExtension>();
                if (ext != null)
                {
                    break;
                }
            }
            string prefix = ext?.texPathPrefix ?? TexPathPrefix;
            bool variants = ext?.lowerVariants ?? true;
            Material[,] result = new Material[TexNames.Length, LowerSuffix.Length];
            for (int i = 1; i < TexNames.Length; i++)
            {
                bool hasLower = variants && (i == (int)CornerType.Corner_NW || i == (int)CornerType.Corner_NE
                                             || i == (int)CornerType.Diagonal_NW || i == (int)CornerType.Diagonal_NE);
                for (int j = 0; j < LowerSuffix.Length; j++)
                {
                    result[i, j] = j == 0 || hasLower
                        ? MaterialPool.MatFrom(prefix + TexNames[i] + LowerSuffix[j], ShaderDatabase.CutoutOverlay)
                        : result[i, 0];
                }
            }
            return result;
        }

        /// <summary>NW/NE 類：缺口旁邊、位置較低的那段牆（NW 是西邊、NE 是東邊）往下接著什麼。</summary>
        private static Lower LowerOf(IntVec3 pos, CornerType cornerType, Map map)
        {
            IntVec3 side;
            switch (cornerType)
            {
                case CornerType.Corner_NW:
                case CornerType.Diagonal_NW:
                    side = IntVec3.West;
                    break;
                case CornerType.Corner_NE:
                case CornerType.Diagonal_NE:
                    side = IntVec3.East;
                    break;
                default:
                    return Lower.None;
            }
            IntVec3 lower = pos + side;
            if (IsHull(lower + IntVec3.South, map))
            {
                return Lower.Linked;
            }
            return IsHull(lower + IntVec3.South + side, map) ? Lower.Diag : Lower.None;
        }

        // 2×2 貼圖的左下角相對缺口格的位置
        private static IntVec3 GetOffset(CornerType cornerType)
        {
            switch (cornerType)
            {
                case CornerType.Corner_NW:
                case CornerType.Diagonal_NW:
                    return new IntVec3(-1, 0, 0);
                case CornerType.Corner_SE:
                case CornerType.Diagonal_SE:
                    return new IntVec3(0, 0, -1);
                case CornerType.Corner_SW:
                case CornerType.Diagonal_SW:
                    return new IntVec3(-1, 0, -1);
                default:
                    return IntVec3.Zero;
            }
        }

        public static bool IsHull(IntVec3 c, Map map)
        {
            Building edifice = c.GetEdificeSafe(map);
            return edifice != null && edifice.def.HasModExtension<ShipHullAngledExtension>();
        }

        private static bool IsHullPlatform(IntVec3 c, Map map)
        {
            return map.terrainGrid.BaseTerrainAt(c)?.affordances?.Contains(DepthRimDefOf.DepthRim_HullPlatform) ?? false;
        }

        private static bool ShouldDrawCornerPiece(IntVec3 pos, Map map, out CornerType cornerType, out Color color)
        {
            cornerType = CornerType.None;
            color = Color.white;
            if (pos.GetEdifice(map) != null)
            {
                return false;
            }
            for (int i = 0; i < Directions.Length; i++)
            {
                tmpChecks[i] = IsHull(pos + Directions[i], map);
            }
            if (tmpChecks[0] && tmpChecks[3] && !tmpChecks[2] && !tmpChecks[1])
            {
                cornerType = tmpChecks[4] ? CornerType.Corner_NW : CornerType.Diagonal_NW;
            }
            else if (tmpChecks[0] && tmpChecks[1] && !tmpChecks[2] && !tmpChecks[3])
            {
                cornerType = tmpChecks[5] ? CornerType.Corner_NE : CornerType.Diagonal_NE;
            }
            else if (tmpChecks[2] && tmpChecks[1] && !tmpChecks[0] && !tmpChecks[3])
            {
                cornerType = tmpChecks[6] ? CornerType.Corner_SE : CornerType.Diagonal_SE;
            }
            else if (tmpChecks[2] && tmpChecks[3] && !tmpChecks[0] && !tmpChecks[1])
            {
                cornerType = tmpChecks[7] ? CornerType.Corner_SW : CornerType.Diagonal_SW;
            }
            if (cornerType == CornerType.None)
            {
                return false;
            }
            // 直角 L 形轉角只補在船體平台外（海水那一側），艙室內角維持直角
            if (cornerType < CornerType.Diagonal_NW && IsHullPlatform(pos, map))
            {
                return false;
            }
            // 顏色取自相鄰的牆，順序同原版：先南北、再東西
            foreach (int i in ColorSourceOrder)
            {
                if (tmpChecks[i])
                {
                    color = (pos + Directions[i]).GetEdificeSafe(map).DrawColor;
                    break;
                }
            }
            return true;
        }

        private static bool IsCornerIndoorMasked(IntVec3 c, CornerType cornerType, Map map)
        {
            switch (cornerType)
            {
                case CornerType.Corner_NE:
                case CornerType.Diagonal_NE:
                    return (c + IntVec3.North).Roofed(map) || (c + IntVec3.East).Roofed(map);
                case CornerType.Corner_NW:
                case CornerType.Diagonal_NW:
                    return (c + IntVec3.North).Roofed(map) || (c + IntVec3.West).Roofed(map);
                case CornerType.Corner_SE:
                case CornerType.Diagonal_SE:
                    return (c + IntVec3.South).Roofed(map) || (c + IntVec3.East).Roofed(map);
                case CornerType.Corner_SW:
                case CornerType.Diagonal_SW:
                    return (c + IntVec3.South).Roofed(map) || (c + IntVec3.West).Roofed(map);
                default:
                    return false;
            }
        }

        private void AddQuad(Material mat, IntVec3 c, Color color, bool addIndoorMask)
        {
            AddQuad(GetSubMesh(mat), c.ToVector3(), color);
            if (addIndoorMask)
            {
                Material indoorMat = MaterialPool.MatFrom(mat.mainTexture as Texture2D, ShaderDatabase.IndoorMaskMasked, mat.color);
                AddQuad(GetSubMesh(indoorMat), c.ToVector3(), color);
            }
        }

        private static void AddQuad(LayerSubMesh sm, Vector3 c, Color color)
        {
            int count = sm.verts.Count;
            for (int i = 0; i < 4; i++)
            {
                sm.verts.Add(new Vector3(c.x + UVs[i].x * CornerScale, CornerAltitude, c.z + UVs[i].y * CornerScale));
                sm.uvs.Add(UVs[i]);
                sm.colors.Add(color);
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
