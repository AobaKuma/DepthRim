using System.Collections;
using System.Collections.Generic;
using RimWorld.Planet;
using Unity.Collections;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海底地形圖的配色：淺水是亮而飽和的青藍，越深越暗、越偏深藍（比照真實的海底地形圖）。
    /// 純函式，可單元測試。色階用公尺，間距在淺處密、深處疏：淺海的差異比深海重要。
    /// </summary>
    public static class Bathymetry
    {
        /// <summary>(深度 m, R, G, B)</summary>
        private static readonly float[,] Stops =
        {
            { 0f, 168f, 232f, 238f },
            { 40f, 112f, 205f, 228f },
            { 200f, 70f, 160f, 210f },
            { 1000f, 52f, 124f, 186f },
            // 2000–6000 m 拉開對比：中洋脊（約 2500 m）和深海平原（約 5000 m）要分得出來
            { 2500f, 40f, 96f, 164f },
            { 4000f, 28f, 66f, 128f },
            { 5500f, 18f, 42f, 94f },
            { 7000f, 12f, 28f, 70f },
            { 11000f, 6f, 14f, 40f }
        };

        /// <summary>覆蓋在生態區貼圖上的不透明度：生態區貼圖仍隱約可見。</summary>
        public const byte OverlayAlpha = 190;

        /// <summary>深度 depth（公尺，海面以下為正）對應的顏色。</summary>
        public static Color32 ColorAt(float depth, byte alpha = OverlayAlpha)
        {
            int last = Stops.GetLength(0) - 1;
            if (depth <= Stops[0, 0])
            {
                return Make(0, 0, 0f, alpha);
            }
            for (int i = 0; i < last; i++)
            {
                if (depth <= Stops[i + 1, 0])
                {
                    float t = (depth - Stops[i, 0]) / (Stops[i + 1, 0] - Stops[i, 0]);
                    return Make(i, i + 1, t, alpha);
                }
            }
            return Make(last, last, 0f, alpha);
        }

        private static Color32 Make(int a, int b, float t, byte alpha)
        {
            byte C(int ch) => (byte)System.Math.Round(Stops[a, ch] + (Stops[b, ch] - Stops[a, ch]) * t);
            return new Color32(C(1), C(2), C(3), alpha);
        }
    }

    /// <summary>
    /// 海洋層的水深著色：依每格的海床深度（負的 <c>Tile.elevation</c>）畫一層半透明的顏色，蓋在生態區貼圖上、海丘圖示之下。
    /// 頂點顏色取「共用這個頂點的所有格子」的平均深度，所以格子之間是平滑的漸層，而不是一格一格的色塊。
    /// 只在選到海洋層時畫（理由同 <see cref="WorldDrawLayer_OceanTerrain"/>）；不建碰撞體，滑鼠選格交給地形層。
    /// </summary>
    public class WorldDrawLayer_OceanBathymetry : WorldDrawLayer
    {
        /// <summary>在地形（3500）之後、海丘圖示（3510）之前。</summary>
        public const int RenderQueue = 3505;

        private static Material material;

        public static Material Material
        {
            get
            {
                if (material == null)
                {
                    material = new Material(WorldMaterials.VertexColorTransparent) { renderQueue = RenderQueue };
                }
                return material;
            }
        }

        public override void Render()
        {
            if (OceanWorldView.Showing(planetLayer))
            {
                base.Render();
            }
        }

        public override IEnumerable Regenerate()
        {
            foreach (object item in base.Regenerate())
            {
                yield return item;
            }
            NativeArray<int> offsets = planetLayer.UnsafeTileIDToVerts_offsets;
            NativeArray<Vector3> verts = planetLayer.UnsafeVerts;
            int tiles = planetLayer.TilesCount;

            // 每個頂點位置的平均深度（相鄰格子的共用頂點座標完全相同，原版地形層也用 == 比對）
            Dictionary<Vector3, Vector2> sums = new Dictionary<Vector3, Vector2>(verts.Length / 3);
            for (int i = 0; i < tiles; i++)
            {
                // 陸塊（大陸）不著色，也不算進海岸頂點的平均：海岸線緊鄰的是淺水的亮色
                if (OceanWorldView.IsLandmass(planetLayer[i].PrimaryBiome))
                {
                    continue;
                }
                float depth = DepthOf(planetLayer[i]);
                int end = i + 1 < offsets.Length ? offsets[i + 1] : verts.Length;
                for (int j = offsets[i]; j < end; j++)
                {
                    sums.TryGetValue(verts[j], out Vector2 s);
                    sums[verts[j]] = new Vector2(s.x + depth, s.y + 1f);
                }
                if (i % 5000 == 0)
                {
                    yield return null;
                }
            }

            Material mat = Material;
            for (int i = 0; i < tiles; i++)
            {
                if (OceanWorldView.IsLandmass(planetLayer[i].PrimaryBiome))
                {
                    continue;
                }
                LayerSubMesh subMesh = GetSubMesh(mat);
                int start = subMesh.verts.Count;
                int end = i + 1 < offsets.Length ? offsets[i + 1] : verts.Length;
                int k = 0;
                for (int j = offsets[i]; j < end; j++)
                {
                    Vector3 v = verts[j];
                    Vector2 s = sums[v];
                    subMesh.verts.Add(v);
                    subMesh.colors.Add(Bathymetry.ColorAt(s.x / s.y));
                    if (j < end - 2)
                    {
                        subMesh.tris.Add(start + k + 2);
                        subMesh.tris.Add(start + k + 1);
                        subMesh.tris.Add(start);
                    }
                    k++;
                }
            }
            FinalizeMesh(MeshParts.All);
        }

        /// <summary>格子的海床深度（公尺）。舊存檔的格子沒有深度（高度 0），改用生態區深度範圍的中間值。</summary>
        public static float DepthOf(Tile tile)
        {
            if (tile.elevation < 0f)
            {
                return -tile.elevation;
            }
            WaterMapExtension ext = tile.PrimaryBiome?.GetModExtension<WaterMapExtension>();
            return ext != null ? -ext.ElevationAt(0.5f) : 0f;
        }
    }
}
