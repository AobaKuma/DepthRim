using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using UnityEngine;

namespace DepthRim.Acceptance
{
    /// <summary>冰洋圖層與升降井（設計稿 §4、DMSE_對接研究 §4）。</summary>
    public static class Tests_World
    {
        public static IEnumerable<Wait> L1_OceanLayer(TestContext ctx)
        {
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            ctx.Require(ocean != null, "ocean layer registered");
            PlanetLayer surface = Find.WorldGrid.Surface;
            ctx.Check(ocean.TilesCount > 0 && ocean.Tiles.Count == ocean.TilesCount, $"ocean layer has generated tiles ({ocean.Tiles.Count}/{ocean.TilesCount})");
            ctx.Check(ocean.Tiles.All(t => t.PrimaryBiome?.HasModExtension<WaterMapExtension>() == true || OceanWorldView.IsLandmass(t.PrimaryBiome)),
                "every ocean tile is an ocean (water map) biome or the landmass");
            ctx.Check(ocean.TilesCount == surface.TilesCount, $"the ocean grid matches the surface grid tile for tile ({ocean.TilesCount}/{surface.TilesCount})");
            ctx.Note($"world coverage {Find.World.info.planetCoverage:P0}, world generated in {CoverageOverride.WorldGenSeconds:0.0} s, managed memory {System.GC.GetTotalMemory(false) / 1048576} MB");
            if (CoverageOverride.Coverage.HasValue)
            {
                ctx.Check(UnityEngine.Mathf.Approximately(Find.World.info.planetCoverage, CoverageOverride.Coverage.Value), $"world generated at the requested coverage ({CoverageOverride.Coverage.Value:P0})");
            }
            // 陸地下方沒有海洋、水下方有海洋；大陸棚是淺的（抽樣）
            int landN = 0, landOk = 0, waterN = 0, waterOk = 0, shelfN = 0, shelfOk = 0;
            var nb = new List<PlanetTile>();
            var misses = new List<string>();
            for (int i = 0; i < surface.TilesCount; i += 13)
            {
                Tile st = surface[i];
                bool ice = st.PrimaryBiome == BiomeDefOf.IceSheet || st.PrimaryBiome == BiomeDefOf.SeaIce;
                PlanetTile under = OceanWorldView.TileBelow(new PlanetTile(i, surface), ocean);
                if (!under.Valid || Find.WorldObjects.MapParentAt(under) != null) { continue; }
                bool landBelow = OceanWorldView.IsLandmass(under.Tile.PrimaryBiome);
                bool isLand = !st.WaterCovered && !ice;
                if (isLand != landBelow && misses.Count < 8)
                {
                    Vector2 ll = Find.WorldGrid.LongLatOf(new PlanetTile(i, surface));
                    float dot = Vector3.Dot(surface.GetTileCenter(i).normalized, ocean.GetTileCenter(under).normalized);
                    misses.Add($"{st.PrimaryBiome.defName}@{ll.y:0.0},{ll.x:0.0} under={under.Tile.PrimaryBiome.defName} dot={dot:0.000000}");
                }
                if (isLand) { landN++; if (landBelow) { landOk++; } continue; }
                waterN++;
                if (!landBelow) { waterOk++; }
                // 結冰區的海岸刻意犬牙交錯（洋盆可以切到岸邊），不算進大陸棚檢查
                if (!st.WaterCovered || ice || st.temperature < OceanBiomeSelector.PolarTemperature) { continue; }
                surface.GetTileNeighbors(new PlanetTile(i, surface), nb);
                if (nb.Any(n => surface[n].PrimaryBiome == BiomeDefOf.IceSheet)) { continue; }
                if (nb.Any(n => !surface[n].WaterCovered))
                {
                    shelfN++;
                    if (under.Tile.elevation >= -200f) { shelfOk++; }
                }
            }
            if (misses.Count > 0) { ctx.Note("mismatches: " + string.Join("; ", misses)); }
            ctx.Note($"samples: land {landOk}/{landN} landmass below, water {waterOk}/{waterN} ocean below, shelf {shelfOk}/{shelfN} within 200 m");
            ctx.Check(landN > 0 && landOk == landN, "dry land has no ocean below (landmass)");
            ctx.Check(waterN > 0 && waterOk == waterN, "water and ice have ocean below");
            ctx.Check(shelfN == 0 || shelfOk >= shelfN * 0.9f, "water next to the coast is continental shelf (200 m or shallower)");
            var byBiome = ocean.Tiles.GroupBy(t => t.PrimaryBiome.defName).OrderByDescending(g => g.Count()).ToList();
            ctx.Note("ocean biomes: " + string.Join(", ", byBiome.Select(g => $"{g.Key} {g.Count()}")));
            ctx.Check(byBiome.Count >= 5, $"the ocean has varied biomes ({byBiome.Count} of {System.Enum.GetValues(typeof(OceanBiome)).Length})");

            // 真實海底地形：中洋脊比深海平原淺、海溝最深而且緊貼海岸、熱液在中洋脊上
            float AvgDepth(string def) { var ts = ocean.Tiles.Where(t => t.PrimaryBiome.defName == def).ToList(); return ts.Count == 0 ? float.NaN : -ts.Average(t => t.elevation); }
            float ridgeDepth = AvgDepth("DepthRim_MidOceanRidge"), plainDepth = AvgDepth("DepthRim_SeabedPlain"), trenchDepth = AvgDepth("DepthRim_AbyssalTrench");
            ctx.Note($"average depth: ridge {ridgeDepth:0} m, abyssal plain {plainDepth:0} m, trench {trenchDepth:0} m");
            ctx.Check(!float.IsNaN(ridgeDepth) && ridgeDepth < plainDepth - 800f, "mid-ocean ridges exist and rise above the abyssal plain");
            ctx.Check(!float.IsNaN(trenchDepth) && trenchDepth > plainDepth + 1500f, "trenches exist and are the deepest places");
            int trenchTiles = 0, trenchNearCoast = 0, ventTiles = 0, ventOnRidge = 0;
            var near = new List<PlanetTile>();
            for (int i = 0; i < ocean.TilesCount; i++)
            {
                string def = ocean[i].PrimaryBiome.defName;
                if (def != "DepthRim_AbyssalTrench" && def != "DepthRim_HydrothermalField") { continue; }
                // 半徑 6 格內有沒有陸地／中洋脊
                bool land = false, ridge = false;
                ocean.Filler.FloodFill(new PlanetTile(i, ocean), t => true, (t, d) =>
                {
                    string b = t.Tile.PrimaryBiome.defName;
                    if (b == "DepthRim_Landmass" || b == "DepthRim_PolarIceCap") { land = true; }
                    if (b == "DepthRim_MidOceanRidge") { ridge = true; }
                    return d >= 6;
                });
                if (def == "DepthRim_AbyssalTrench") { trenchTiles++; if (land) { trenchNearCoast++; } }
                else { ventTiles++; if (ridge) { ventOnRidge++; } }
                if (trenchTiles > 300 && ventTiles > 100) { break; }
            }
            ctx.Note($"trench tiles within 6 tiles of land: {trenchNearCoast}/{trenchTiles}; vent tiles next to ridge: {ventOnRidge}/{ventTiles}");
            ctx.Check(trenchTiles > 0 && trenchNearCoast == trenchTiles, "every trench runs along a coast (subduction margin)");
            ctx.Check(ventTiles == 0 || ventOnRidge >= ventTiles * 0.9f, "hydrothermal vents sit on mid-ocean ridges");

            // 極地冰蓋與深洋盆犬牙交錯：有離陸地很遠的冰舌，也有緊貼海岸的深洋盆海灣（結冰區）
            int tongues = 0, embayments = 0, iceBoundary = 0, polarWater = 0;
            for (int i = 0; i < ocean.TilesCount && (tongues < 50 || embayments < 50 || polarWater < 4000); i++)
            {
                Tile ot = ocean[i];
                Tile st = surface[i];
                bool frozenWater = st.WaterCovered && (st.PrimaryBiome == BiomeDefOf.SeaIce || st.temperature < OceanBiomeSelector.PolarTemperature);
                bool polar = ot.PrimaryBiome.defName == "DepthRim_PolarIceCap";
                if (!frozenWater) { continue; }
                if (polar) { polarWater++; }
                PlanetTile pt = new PlanetTile(i, ocean);
                ocean.GetTileNeighbors(pt, near);
                if (polar && near.Any(n => n.Tile.elevation < -2500f)) { iceBoundary++; }
                bool deep = ot.elevation < -2500f;
                if (!polar && !deep) { continue; }
                bool landNear = false, landAdjacent = near.Any(n => OceanWorldView.IsLandmass(n.Tile.PrimaryBiome) || !surface[n.tileId].WaterCovered);
                if (polar)
                {
                    ocean.Filler.FloodFill(pt, t => true, (t, d) =>
                    {
                        if (!surface[t.tileId].WaterCovered) { landNear = true; }
                        return landNear || d >= 4;
                    });
                    if (!landNear) { tongues++; }
                }
                else if (landAdjacent) { embayments++; }
            }
            ctx.Note($"ice edge: {tongues} ice-tongue tiles (no land within 4), {embayments} deep-basin tiles touching the coast, {iceBoundary} cap tiles bordering the deep basin, {polarWater} cap tiles over frozen sea");
            if (polarWater > 50)
            {
                ctx.Check(tongues > 0, "ice tongues reach out over the deep basin");
                ctx.Check(embayments > 0, "the deep basin cuts into the ice cap right up to the coast");
            }
            ctx.Check(ocean.Tiles.All(t => t.hilliness != Hilliness.Undefined), "ocean tiles have hilliness for rock generation");
            // 依正上方地表決定：冰原（陸上的冰）下方都是極地冰蓋（抽樣）；海冰只有在大陸棚上才是

            int iceChecked = 0, iceOk = 0;
            for (int i = 0; i < surface.TilesCount && iceChecked < 40; i += 97)
            {
                Tile st = surface[i];
                if (st.PrimaryBiome != BiomeDefOf.IceSheet) { continue; }
                iceChecked++;
                PlanetTile under = OceanWorldView.TileBelow(new PlanetTile(i, surface), ocean);
                if (under.Valid && under.Tile.PrimaryBiome.defName == "DepthRim_PolarIceCap") { iceOk++; }
            }
            ctx.Note($"ice sheet samples: {iceOk}/{iceChecked} have polar ice cap below");
            ctx.Check(iceChecked == 0 || iceOk == iceChecked, "the ice sheet lies over the polar ice cap");
            ctx.Check(surface.zoomInToLayer == ocean, "zooming in on the surface goes to the ice ocean");
            ctx.Check(ocean.zoomOutToLayer == surface, "zooming out of the ice ocean goes to the surface");
            ctx.Check(!surface.HasConnectionFromTo(ocean) && !ocean.HasConnectionFromTo(surface), "no gravship/shuttle connection between surface and ocean");
            ctx.Check(Find.WorldGrid.PlanetLayers.Values.Count(l => l.Def == DepthRimDefOf.DepthRim_Ocean) == 1, "exactly one ice ocean layer");
            ctx.Check(!DMSE.TransferEnvironment.AllowsTransfer(DepthRimDefOf.DepthRim_Ocean), "DMSE transfer flight is not enabled on the ocean yet (submarine prototype pending)");
            int seabed = 0;
            for (int i = 0; i < ocean.TilesCount; i += 7)
            {
                if (DepthTierUtility.MaxTierAt(new PlanetTile(i, ocean)) == DepthTier.Seabed) { seabed++; }
            }
            ctx.Note($"sampled seabed-tier tiles: {seabed} of {ocean.TilesCount / 7 + 1}");
            yield break;
        }

        /// <summary>世界地圖上的海洋地形：生態區貼圖、海床深度、只在選到海洋層時畫、看海洋時地表藏起來。</summary>
        public static IEnumerable<Wait> L2_OceanWorldMap(TestContext ctx)
        {
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            ctx.Require(ocean != null, "ocean layer registered");
            PlanetLayer surface = Find.WorldGrid.Surface;
            foreach (BiomeDef biome in ocean.Tiles.Select(t => t.PrimaryBiome).Distinct())
            {
                UnityEngine.Material mat = biome.DrawMaterial;
                ctx.Check(mat != null && mat.mainTexture != null && mat.mainTexture != BaseContent.BadTex, $"{biome.defName} has a world-map material with its texture");
            }
            ctx.Check(ocean.Tiles.Where(t => !OceanWorldView.IsLandmass(t.PrimaryBiome)).All(t => t.elevation < 0f), "every ocean tile lies below sea level (negative elevation)");
            ctx.Check(ocean.Tiles.Where(t => OceanWorldView.IsLandmass(t.PrimaryBiome)).All(t => t.elevation > 0f), "landmass tiles are above sea level");
            var depthByBiome = ocean.Tiles.Where(t => !OceanWorldView.IsLandmass(t.PrimaryBiome)).GroupBy(t => t.PrimaryBiome.defName).Select(g => $"{g.Key} {-g.Max(t => t.elevation):0}–{-g.Min(t => t.elevation):0}m");
            ctx.Note("seafloor depth: " + string.Join(", ", depthByBiome));
            ctx.Check(ocean.WorldDrawLayers.Any(l => l is WorldDrawLayer_OceanTerrain) && ocean.WorldDrawLayers.Any(l => l is WorldDrawLayer_OceanHills),
                "the ocean layer has DepthRim's terrain and hills draw layers");

            PlanetTile home = OceanWorldView.TileBelow(DepthRimFixture.Home.Tile, ocean);
            CameraJumper.TryJump(new GlobalTargetInfo(home));
            yield return new WaitFrames(5);
            PlanetLayer.Selected = surface;
            yield return new WaitFrames(5);
            ctx.Check(surface.Visible && ocean.Visible, "viewing the surface: both layers render (ocean terrain stays hidden under it)");
            ctx.Check(!OceanWorldView.Showing(ocean), "viewing the surface: the ocean terrain is not drawn");
            PlanetLayer.Selected = ocean;
            Find.WorldSelector.SelectedTile = home;
            yield return new WaitFrames(10);
            ctx.Check(ocean.Visible && !surface.Visible, "viewing the ocean: the surface is hidden so the seabed shows");
            ctx.Check(OceanWorldView.Showing(ocean), "viewing the ocean: the ocean terrain is drawn");
            WorldDrawLayer_OceanTerrain terrain = ocean.WorldDrawLayers.OfType<WorldDrawLayer_OceanTerrain>().FirstOrDefault();
            ctx.Check(terrain != null && !terrain.Dirty, "the ocean terrain mesh has been built");
            WorldDrawLayer_OceanBathymetry bathymetry = ocean.WorldDrawLayers.OfType<WorldDrawLayer_OceanBathymetry>().FirstOrDefault();
            ctx.Check(bathymetry != null && !bathymetry.Dirty, "the depth shading mesh has been built");
            WorldDrawLayer_OceanLand landLayer = ocean.WorldDrawLayers.OfType<WorldDrawLayer_OceanLand>().FirstOrDefault();
            ctx.Check(landLayer != null && !landLayer.Dirty, "the landmass (continents) mesh has been built");
            // 各圖層重建一次要多久（切到海洋層時，髒的圖層會在長事件裡重建）
            foreach (WorldDrawLayer layer in ocean.WorldDrawLayers)
            {
                if (!(layer is WorldDrawLayer_OceanTerrain || layer is WorldDrawLayer_OceanBathymetry || layer is WorldDrawLayer_OceanLand || layer is WorldDrawLayer_OceanHills)) { continue; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                layer.RegenerateNow();
                ctx.Note($"rebuild {layer.GetType().Name}: {sw.Elapsed.TotalMilliseconds:0} ms");
            }
            yield return new WaitFrames(5);
            ctx.Screenshot("ocean_world_map");
            yield return new WaitFrames(5);
            // 拉遠看整片海底地形圖
            WorldCameraDriver cam = Find.WorldCameraDriver;
            float far = HarmonyLib.Traverse.Create(typeof(WorldCameraDriver)).Field("MaxAltitude").GetValue<float>() * 0.6f;
            HarmonyLib.Traverse.Create(cam).Field("desiredAltitude").SetValue(far);
            HarmonyLib.Traverse.Create(cam).Field("altitude").SetValue(far);
            yield return new WaitFrames(30);
            ctx.Screenshot("ocean_world_map_wide");
            yield return new WaitFrames(10);
            // 對準一段中洋脊，看它是不是一條連續的隆起
            PlanetTile ridgeTile = PlanetTile.Invalid;
            for (int i = 0; i < ocean.TilesCount; i++)
            {
                if (ocean[i].PrimaryBiome.defName == "DepthRim_MidOceanRidge") { ridgeTile = new PlanetTile(i, ocean); break; }
            }
            if (ridgeTile.Valid)
            {
                Find.WorldCameraDriver.JumpTo(ridgeTile);
                float mid = HarmonyLib.Traverse.Create(typeof(WorldCameraDriver)).Field("MaxAltitude").GetValue<float>() * 0.35f;
                HarmonyLib.Traverse.Create(cam).Field("desiredAltitude").SetValue(mid);
                HarmonyLib.Traverse.Create(cam).Field("altitude").SetValue(mid);
                yield return new WaitFrames(30);
                ctx.Screenshot("ocean_world_map_ridge");
                yield return new WaitFrames(10);
            }
            // 對準極地冰蓋與深洋盆的交界（冰緣），看它是不是犬牙交錯
            PlanetTile iceEdge = PlanetTile.Invalid;
            var edgeNb = new List<PlanetTile>();
            for (int i = 0; i < ocean.TilesCount && !iceEdge.Valid; i++)
            {
                if (ocean[i].PrimaryBiome.defName != "DepthRim_PolarIceCap" || !surface[i].WaterCovered) { continue; }
                ocean.GetTileNeighbors(new PlanetTile(i, ocean), edgeNb);
                if (edgeNb.Count(n => n.Tile.elevation < -2500f) >= 2) { iceEdge = new PlanetTile(i, ocean); }
            }
            if (iceEdge.Valid)
            {
                Find.WorldCameraDriver.JumpTo(iceEdge);
                float mid = HarmonyLib.Traverse.Create(typeof(WorldCameraDriver)).Field("MaxAltitude").GetValue<float>() * 0.3f;
                HarmonyLib.Traverse.Create(cam).Field("desiredAltitude").SetValue(mid);
                HarmonyLib.Traverse.Create(cam).Field("altitude").SetValue(mid);
                yield return new WaitFrames(30);
                ctx.Screenshot("ocean_world_map_ice_edge");
            }
            // 截圖晚一幀才存檔；L2 單獨跑時是最後一個測試，等久一點再離開世界地圖
            yield return new WaitFrames(30);
            PlanetLayer.Selected = surface;
            CameraJumper.TryHideWorld();
            yield return new WaitFrames(3);
        }

        public static IEnumerable<Wait> E1_IceShaft(TestContext ctx)
        {
            Map ocean = DepthRimFixture.EnsureOcean();
            ctx.Require(ocean != null, "ice shaft generated a map");
            yield return new WaitFrames(5);
            Building_IceShaft shaft = DepthRimFixture.Shaft;
            ctx.Check(ocean.Tile.LayerDef == DepthRimDefOf.DepthRim_Ocean, "map is on the ice ocean layer");
            ctx.Check(ocean.Tile == OceanWorldView.TileBelow(DepthRimFixture.Home.Tile, DepthRimFixture.OceanLayer), "map is under the surface colony");
            ctx.Check(ocean.Parent.def == DepthRimDefOf.DepthRim_OceanSite, "map parent is an ocean site");
            bool iconOk;
            try { _ = ocean.Parent.ExpandingIconColor; iconOk = true; }
            catch (System.Exception) { iconOk = false; }
            ctx.Check(iconOk, "the ocean site's world-map icon colour can be read (no NullReference while drawing)");
            ctx.Check(ocean.Parent.Faction == Faction.OfPlayer, "ice ocean site belongs to the player");
            ctx.Check(ocean.Biome.defName == "DepthRim_PolarIceCap", "map uses the ice ocean biome");
            ctx.Check(ocean.GetComponent<MapComponent_Flooding>().Active, "flooding is active on the ocean map");
            ctx.Require(shaft.exit != null && shaft.exit.Spawned && shaft.exit.Map == ocean, "landing spawned on the ocean map");
            ctx.Check(shaft.exit.def == DepthRimFixture.IceShaftBottom, "landing uses DepthRim_IceShaftBottom");
            ctx.Check(shaft.exit.entrance == shaft, "landing is linked back to the shaft");
            // PocketMapExit.GetOtherMap() 就是 entrance.Map，所以這條等於「出口的入口井還在地表那張圖上」
            Map other = shaft.exit.GetOtherMap();
            ctx.Check(other == DepthRimFixture.Home,
                $"landing leads back to the surface ({other?.uniqueID.ToString() ?? "null"} vs home {DepthRimFixture.Home.uniqueID}, shaft spawned={shaft.Spawned})");
            bool clear = GenRadial.RadialCellsAround(shaft.exit.Position, 2.9f, true)
                .Where(c => c.InBounds(ocean))
                .All(c => !(c.GetEdifice(ocean)?.def.mineable ?? false));
            ctx.Check(clear, "rock cleared around the landing");
            ctx.Check(shaft.GetOtherMap() == ocean, "entering again reuses the same map");
            int rocks = ocean.listerThings.AllThings.Count(t => t.def.mineable);
            ctx.Check(rocks > 100, $"ocean map has rock ({rocks} mineable)");
            Current.Game.CurrentMap = ocean;
            CameraJumper.TryJump(shaft.exit);
            yield return new WaitFrames(10);
            ctx.Screenshot("ocean_landing");
        }
    }
}
