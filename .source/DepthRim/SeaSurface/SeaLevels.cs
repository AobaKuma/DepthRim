using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海面的兩層地圖（偽多層）：
    /// <list type="bullet">
    /// <item><b>水面層</b>：海面地點（<see cref="WorldObject_SeaSurfaceSite"/>）的地圖。開放海面（<see cref="DepthRimDefOf.DepthRim_OpenSea"/>）上可以鋪浮在水上的船體平台、蓋平台支柱。</item>
    /// <item><b>平台層</b>：同尺寸、座標對齊的口袋地圖（<see cref="PocketMapParent"/>，來源地圖是水面層），第一根支柱蓋好時建立。
    /// 沒鋪平台的地方是高空（<see cref="DepthRimDefOf.DepthRim_OpenAir"/>），從那裡看下去是 <see cref="ViewDrop"/> 格下方的海。</item>
    /// </list>
    /// 兩層之間只靠支柱（升降梯）往來；畫面上互相看得見：平台層畫出水面層的地形與建築（<see cref="SectionLayer_LowerLevel"/>），
    /// 水面層畫出平台的影子與往上伸的支柱。
    /// </summary>
    public static class SeaLevels
    {
        public static bool IsSeaLevel(Map map)
        {
            return map?.Parent is WorldObject_SeaSurfaceSite;
        }

        public static bool IsPlatformLevel(Map map)
        {
            return map?.Parent is PocketMapParent p && p.mapGenerator == DepthRimDefOf.DepthRim_PlatformLevel && IsSeaLevel(p.sourceMap);
        }

        /// <summary>海面的任一層（水面層或平台層）。</summary>
        public static bool IsAnyLevel(Map map)
        {
            return IsSeaLevel(map) || IsPlatformLevel(map);
        }

        /// <summary>水面層對應的平台層（還沒建立時為 null）。</summary>
        public static Map PlatformLevelOf(Map seaLevel)
        {
            if (seaLevel == null)
            {
                return null;
            }
            return Find.Maps.FirstOrDefault(m => m.Parent is PocketMapParent p && p.sourceMap == seaLevel && p.mapGenerator == DepthRimDefOf.DepthRim_PlatformLevel);
        }

        /// <summary>平台層對應的水面層。</summary>
        public static Map SeaLevelOf(Map platformLevel)
        {
            return IsPlatformLevel(platformLevel) ? ((PocketMapParent)platformLevel.Parent).sourceMap : null;
        }

        /// <summary>另一層（水面層 ↔ 平台層）。</summary>
        public static Map OtherLevel(Map map)
        {
            if (IsSeaLevel(map))
            {
                return PlatformLevelOf(map);
            }
            return SeaLevelOf(map);
        }

        /// <summary>從這張地圖看下去，海面在畫面上往南偏幾格：平台層是平台離海面的高度，水面層是 0。</summary>
        public static float ViewDrop(Map map)
        {
            return IsPlatformLevel(map) ? MapComponent_SeaSurface.SeaDropCells : 0f;
        }

        /// <summary>原版地表地圖（例如海岸殖民地）：在地表圖層上、但不是海面的兩層。</summary>
        public static bool IsVanillaSurface(Map map)
        {
            return map != null && !IsAnyLevel(map) && map.Tile.Valid && map.Tile.LayerDef == PlanetLayerDefOf.Surface;
        }

        /// <summary>
        /// 這張地圖上船體離開或平台拆掉時，DMSE 鋪的太空要換成 <see cref="VoidTerrain"/>（或記下的原本地形）。
        /// 海洋層不在這裡：DMSE 直接讀 <c>TransferEnvironmentExtension.voidTerrain</c>（深淵），不會鋪太空。
        /// </summary>
        public static bool ReplacesSpace(Map map)
        {
            return IsAnyLevel(map) || IsVanillaSurface(map);
        }

        /// <summary>
        /// 這張地圖上的「空」地形：水面層是開放海面，平台層是高空，原版地表地圖是原版深海水
        /// （有 <see cref="MapComponent_SeabedMemory"/> 記錄時優先還原記下的地形，這裡只是沒有記錄時的退路）。
        /// </summary>
        public static TerrainDef VoidTerrain(Map map)
        {
            if (IsPlatformLevel(map))
            {
                return DepthRimDefOf.DepthRim_OpenAir;
            }
            if (IsVanillaSurface(map))
            {
                return TerrainDefOf.WaterOceanDeep;
            }
            return DepthRimDefOf.DepthRim_OpenSea;
        }

        /// <summary>
        /// 建立水面層的平台層（已經有就直接回傳）。比照 <see cref="PocketMapUtility.GeneratePocketMap"/>，但口袋地圖的地點設在水面層的格子上，
        /// 天空、日照、季節才和水面層一致。由支柱（<see cref="Building_PillarFoot"/>）在 <see cref="PocketMapUtility.currentlyGeneratingPortal"/> 期間呼叫。
        /// </summary>
        public static Map GetOrGeneratePlatformLevel(Map seaLevel)
        {
            Map existing = PlatformLevelOf(seaLevel);
            if (existing != null)
            {
                return existing;
            }
            PocketMapParent parent = (PocketMapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.PocketMap);
            parent.sourceMap = seaLevel;
            parent.mapGenerator = DepthRimDefOf.DepthRim_PlatformLevel;
            parent.Tile = seaLevel.Tile;
            Map map = MapGenerator.GenerateMap(seaLevel.Size, parent, DepthRimDefOf.DepthRim_PlatformLevel, null, null, isPocketMap: true);
            Find.World.pocketMaps.Add(parent);
            return map;
        }
    }

    /// <summary>平台層整張鋪成高空（沒有迷霧）。平台由支柱生成時開出。</summary>
    public class GenStep_PlatformLevel : GenStep
    {
        public override int SeedPart => 0x5EA5F3;

        public override void Generate(Map map, GenStepParams parms)
        {
            foreach (IntVec3 c in map.AllCells)
            {
                map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_OpenAir);
            }
        }
    }

    /// <summary>平台層是口袋地圖，原版給口袋地圖固定溫度；平台層在露天，用水面層的室外溫度。</summary>
    [HarmonyPatch(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp), MethodType.Getter)]
    public static class Patch_PlatformLevelTemperature
    {
        [HarmonyPostfix]
        public static void Postfix(Map ___map, ref float __result)
        {
            Map sea = SeaLevels.SeaLevelOf(___map);
            if (sea != null)
            {
                __result = sea.mapTemperature.OutdoorTemp;
            }
        }
    }

    /// <summary>
    /// 水面層的地形或建築改變時，平台層畫的「往下看」快照要重建（不即時：平台層每兩秒檢查一次版本號，見 <see cref="MapComponent_SeaSurface"/>）。
    /// </summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty), typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool))]
    public static class Patch_SeaLevelChanged
    {
        [HarmonyPostfix]
        public static void Postfix(Map ___map, IntVec3 loc, ulong dirtyFlags)
        {
            if ((dirtyFlags & (ulong)MapMeshFlagDefOf.Terrain) != 0 && SeaLevels.IsPlatformLevel(___map))
            {
                // 平台層的甲板變了：水面層的平台影子（畫面與遊戲邏輯的光照）要重算
                SeaLevels.SeaLevelOf(___map)?.GetComponent<MapComponent_SeaSurface>()?.MarkDirty();
                return;
            }
            if (!(___map?.Parent is WorldObject_SeaSurfaceSite))
            {
                return;
            }
            if ((dirtyFlags & (ulong)MapMeshFlagDefOf.GroundGlow) != 0)
            {
                // 水面層的燈光變了：平台層往下看時，那一帶的平台影子與柱身（頂點色的燈光）要重建；柱身很長，鄰近區塊一起
                Map platform = SeaLevels.PlatformLevelOf(___map);
                if (platform != null)
                {
                    IntVec3 screen = SectionLayer_LowerLevel.ScreenCell(loc, MapComponent_SeaSurface.SeaDropCells);
                    if (screen.InBounds(platform))
                    {
                        platform.mapDrawer.MapMeshDirty(screen, (ulong)MapMeshFlagDefOf.GroundGlow, regenAdjacentCells: true, regenAdjacentSections: true);
                    }
                }
            }
            if ((dirtyFlags & ((ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings)) == 0)
            {
                return;
            }
            MapComponent_SeaSurface comp = ___map.GetComponent<MapComponent_SeaSurface>();
            if (comp != null)
            {
                if ((dirtyFlags & (ulong)MapMeshFlagDefOf.Terrain) != 0)
                {
                    comp.MarkDirty();
                }
                comp.version++;
            }
        }
    }
}
