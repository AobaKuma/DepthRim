using System.Collections;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海洋層在世界地圖上的地形：每一格畫生態區貼圖（<c>BiomeDef.texture</c>），和地表一樣。
    /// 海洋層半徑 97、在地表（100）裡面，所以只在選到海洋層時才畫；地表那時由
    /// <see cref="Patch_PlanetLayer_Visible"/> 整層藏起來，否則海床會被地表蓋住。
    /// <para>不覆寫 <c>Visible</c>：原版只要圖層可見、地形層是髒的，就會開長事件重建；
    /// 而重建又會跳過不可見的層，覆寫 <c>Visible</c> 會變成每一幀都開長事件。所以只擋 <c>Render</c>。</para>
    /// </summary>
    public class WorldDrawLayer_OceanTerrain : WorldDrawLayer_Terrain
    {
        public override void Render()
        {
            if (OceanWorldView.Showing(planetLayer))
            {
                base.Render();
            }
        }
    }

    /// <summary>
    /// 海洋層的地勢圖示（海丘、海山）：只在選到海洋層時畫，理由同 <see cref="WorldDrawLayer_OceanTerrain"/>。
    /// 擺放邏輯照原版 <see cref="WorldDrawLayer_Hills"/>，但改用偏冷、半透明的材質：
    /// 原版的地勢圖示是陸地的褐色，畫在海床上看起來像一座座小島。
    /// </summary>
    [StaticConstructorOnStartup]
    public class WorldDrawLayer_OceanHills : WorldDrawLayer
    {
        private static readonly Color Tint = new Color(0.30f, 0.40f, 0.56f, 0.42f);

        private static readonly Material SmallHills = MaterialPool.MatFrom("World/Hills/SmallHills", ShaderDatabase.WorldOverlayTransparentLit, Tint, 3510);

        private static readonly Material LargeHills = MaterialPool.MatFrom("World/Hills/LargeHills", ShaderDatabase.WorldOverlayTransparentLit, Tint, 3510);

        private static readonly Material Mountains = MaterialPool.MatFrom("World/Hills/Mountains", ShaderDatabase.WorldOverlayTransparentLit, Tint, 3510);

        private static readonly FloatRange BaseSizeRange = new FloatRange(0.9f, 1.1f);

        private static readonly FloatRange OffsetSmall = new FloatRange(0f, 0.37f);

        private static readonly FloatRange OffsetLarge = new FloatRange(0f, 0.2f);

        private static readonly FloatRange OffsetMountains = new FloatRange(0f, 0.08f);

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
            Rand.PushState();
            Rand.Seed = Find.World.info.Seed ^ planetLayer.LayerID;
            for (int i = 0; i < planetLayer.TilesCount; i++)
            {
                Tile tile = planetLayer[i];
                // 陸塊（大陸）用原版的褐色地勢圖示，和地表一樣；海床用偏冷的半透明圖示
                bool land = OceanWorldView.IsLandmass(tile.PrimaryBiome);
                Material material;
                FloatRange offset;
                switch (tile.hilliness)
                {
                    case Hilliness.SmallHills:
                        material = land ? WorldMaterials.SmallHills : SmallHills;
                        offset = OffsetSmall;
                        break;
                    case Hilliness.LargeHills:
                        material = land ? WorldMaterials.LargeHills : LargeHills;
                        offset = OffsetLarge;
                        break;
                    case Hilliness.Mountainous:
                        material = land ? WorldMaterials.Mountains : Mountains;
                        offset = OffsetMountains;
                        break;
                    case Hilliness.Impassable:
                        material = land ? WorldMaterials.ImpassableMountains : Mountains;
                        offset = OffsetMountains;
                        break;
                    default:
                        continue;
                }
                LayerSubMesh subMesh = GetSubMesh(material);
                Vector3 center = planetLayer.GetTileCenter(new PlanetTile(i, planetLayer));
                Vector3 tangentsPos = center;
                float magnitude = center.magnitude;
                center = (center + Rand.UnitVector3 * (offset.RandomInRange * planetLayer.AverageTileSize)).normalized * magnitude;
                WorldRendererUtility.PrintQuadTangentialToPlanet(center, tangentsPos, BaseSizeRange.RandomInRange * planetLayer.GetTileSize(tile.tile), 0.005f, subMesh, counterClockwise: false, Rand.Range(0f, 360f), printUVs: false);
                WorldRendererUtility.PrintTextureAtlasUVs(Rand.Range(0, 2), Rand.Range(0, 2), 2, 2, subMesh);
            }
            Rand.PopState();
            FinalizeMesh(MeshParts.All);
        }
    }

    /// <summary>世界覆蓋率不到 100% 時，海洋層沒有生成的部分（灰色球殼）：只在選到海洋層時畫。</summary>
    public class WorldDrawLayer_OceanUngenerated : WorldDrawLayer_UngeneratedPlanetParts
    {
        public override void Render()
        {
            if (OceanWorldView.Showing(planetLayer))
            {
                base.Render();
            }
        }
    }

    public static class OceanWorldView
    {
        /// <summary>現在選到的是海洋層。</summary>
        public static bool OceanSelected => PlanetLayer.Selected?.Def == DepthRimDefOf.DepthRim_Ocean;

        /// <summary>
        /// 地表格正下方的海洋格。新世界的海洋層與地表細分相同、格子 ID 一一對應（同一個方向），直接用同一個 ID；
        /// 原版 <c>GetClosestTile_NewTemp</c> 在這種情況常常回傳鄰格（正下方是陸地，卻拿到旁邊的海）。
        /// 舊存檔的海洋層比較粗，才退回原版的搜尋。
        /// </summary>
        public static PlanetTile TileBelow(PlanetTile above, PlanetLayer ocean = null)
        {
            ocean = ocean ?? Find.WorldGrid?.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean);
            if (ocean == null || !above.Valid)
            {
                return PlanetTile.Invalid;
            }
            PlanetLayer from = above.Layer;
            if (from == ocean)
            {
                return above;
            }
            if (from != null && from.TilesCount == ocean.TilesCount && above.tileId < ocean.TilesCount)
            {
                PlanetTile same = new PlanetTile(above.tileId, ocean);
                if (Vector3.Dot(from.GetTileCenter(above).normalized, ocean.GetTileCenter(same).normalized) > 0.999999f)
                {
                    return same;
                }
            }
            return ocean.GetClosestTile_NewTemp(above);
        }

        /// <summary>海洋層的陸塊（大陸下方，沒有海洋）。</summary>
        public static bool IsLandmass(BiomeDef biome)
        {
            return biome != null && biome == DepthRimDefOf.DepthRim_Landmass;
        }

        /// <summary>這個圖層是目前選到的海洋層。</summary>
        public static bool Showing(PlanetLayer layer)
        {
            return layer != null && PlanetLayer.Selected == layer && layer.Def == DepthRimDefOf.DepthRim_Ocean;
        }
    }

    /// <summary>
    /// 看海洋層時，其他圖層（地表、軌道）整層不畫：海洋層在地表裡面，地表的地形、雲、發光會把海床整個蓋住。
    /// 原版的 <see cref="PlanetLayer.Visible"/> 永遠是 true，只用在 <see cref="WorldRenderer"/> 決定畫哪些圖層；
    /// 圖層不可見時它的地形也不會觸發重建，切回地表時才重建。
    /// </summary>
    [HarmonyPatch(typeof(PlanetLayer), nameof(PlanetLayer.Visible), MethodType.Getter)]
    public static class Patch_PlanetLayer_Visible
    {
        [HarmonyPostfix]
        public static void Postfix(PlanetLayer __instance, ref bool __result)
        {
            if (__result && OceanWorldView.OceanSelected && __instance != PlanetLayer.Selected)
            {
                __result = false;
            }
        }
    }
}
