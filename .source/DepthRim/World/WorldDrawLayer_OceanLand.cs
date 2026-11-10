using System.Collections;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Unity.Collections;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海洋層的陸塊格（大陸下方，沒有海洋）畫成正上方的地表生態區，海洋層上才看得出大陸與海岸線（比照真實的海底地形圖）。
    /// 海洋層和地表的細分相同，每個陸塊格正好在一個地表格下方。
    /// 材質複製地表生態區的世界材質，繪製順序放在海洋地形（3500）之後、水深著色（3505）之前，蓋掉陸塊的退路貼圖。
    /// 只在選到海洋層時畫；不建碰撞體，滑鼠選格交給地形層。
    /// </summary>
    public class WorldDrawLayer_OceanLand : WorldDrawLayer
    {
        public const int RenderQueue = 3502;

        private static readonly Dictionary<BiomeDef, Material> materials = new Dictionary<BiomeDef, Material>();

        public static Material MaterialFor(BiomeDef biome)
        {
            if (biome?.DrawMaterial == null)
            {
                return null;
            }
            if (!materials.TryGetValue(biome, out Material mat) || mat == null)
            {
                mat = new Material(biome.DrawMaterial) { renderQueue = RenderQueue };
                materials[biome] = mat;
            }
            return mat;
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
            PlanetLayer surface = Find.WorldGrid?.Surface;
            if (surface == null || surface.Tiles == null || surface.Tiles.Count == 0)
            {
                yield break;
            }
            // 細分相同（新世界）時格子 ID 一一對應，不用建搜尋表；建表在 100% 覆蓋率下要一秒多
            SurfaceTileLookup lookup = surface.TilesCount == planetLayer.TilesCount ? null : SurfaceTileLookup.Build(surface);
            NativeArray<int> offsets = planetLayer.UnsafeTileIDToVerts_offsets;
            NativeArray<Vector3> verts = planetLayer.UnsafeVerts;
            int drawn = 0;
            for (int i = 0; i < planetLayer.TilesCount; i++)
            {
                if (!OceanWorldView.IsLandmass(planetLayer[i].PrimaryBiome))
                {
                    continue;
                }
                PlanetTile ptile = new PlanetTile(i, planetLayer);
                int above = lookup != null ? lookup.ClosestTo(planetLayer.GetTileCenter(ptile).normalized) : i;
                Tile land = above >= 0 ? surface[above] : null;
                Material mat = MaterialFor(land?.PrimaryBiome);
                if (mat == null)
                {
                    continue;
                }
                LayerSubMesh subMesh = GetSubMesh(mat);
                int start = subMesh.verts.Count;
                int end = i + 1 < offsets.Length ? offsets[i + 1] : verts.Length;
                Vector3 elevation = new Vector3(land.elevation, land.elevation, 0f);
                int k = 0;
                for (int j = offsets[i]; j < end; j++)
                {
                    subMesh.verts.Add(verts[j]);
                    subMesh.uvs.Add(elevation);
                    if (j < end - 2)
                    {
                        subMesh.tris.Add(start + k + 2);
                        subMesh.tris.Add(start + k + 1);
                        subMesh.tris.Add(start);
                    }
                    k++;
                }
                if (++drawn % 5000 == 0)
                {
                    yield return null;
                }
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}
