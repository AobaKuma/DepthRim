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
                bool ice = st.PrimaryBiome == BiomeDefOf.SeaIce; // 冰原算陸地（2026-11-11）
                PlanetTile under = OceanWorldView.TileBelow(new PlanetTile(i, surface), ocean);
                if (!under.Valid || Find.WorldObjects.MapParentAt(under) != null) { continue; }
                bool landBelow = OceanWorldView.IsLandmass(under.Tile.PrimaryBiome);
                bool isLand = !st.WaterCovered;
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
            ctx.Check(landN > 0 && landOk == landN, "land, ice sheets included, has no ocean below (landmass)");
            ctx.Check(waterN > 0 && waterOk == waterN, "water and sea ice have ocean below");
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
                    if (b == "DepthRim_Landmass") { land = true; }
                    // 窄段的中洋脊只有軸線（2026-11-11 脊寬沿著脊變化），熱液段旁邊可能只有其他熱液格
                    if (b == "DepthRim_MidOceanRidge" || (b == "DepthRim_HydrothermalField" && t.tileId != i)) { ridge = true; }
                    return d >= 6;
                });
                if (def == "DepthRim_AbyssalTrench") { trenchTiles++; if (land) { trenchNearCoast++; } }
                else { ventTiles++; if (ridge) { ventOnRidge++; } }
                if (trenchTiles > 300 && ventTiles > 100) { break; }
            }
            ctx.Note($"trench tiles within 6 tiles of land: {trenchNearCoast}/{trenchTiles}; vent tiles next to ridge: {ventOnRidge}/{ventTiles}");
            ctx.Check(trenchTiles > 0 && trenchNearCoast == trenchTiles, "every trench runs along a coast (subduction margin)");
            ctx.Check(ventTiles == 0 || ventOnRidge >= ventTiles * 0.9f, "hydrothermal vents sit on mid-ocean ridges");

            ctx.Check(ocean.Tiles.All(t => t.hilliness != Hilliness.Undefined), "ocean tiles have hilliness for rock generation");
            // 靠近陸地邊緣：海洋視圖的深度和地表視圖一樣（生態區深度範圍夾住的除外），往外漸變（2026-11-11）
            if (surface.TilesCount == ocean.TilesCount)
            {
                int coastN = 0, coastSame = 0, coastClamped = 0;
                float coastSurfaceSum = 0f;
                var cnb = new List<PlanetTile>();
                for (int i = 0; i < ocean.TilesCount; i++)
                {
                    Tile st = surface[i], ot = ocean[i];
                    if (!st.WaterCovered || OceanWorldView.IsLandmass(ot.PrimaryBiome) || Find.WorldObjects.MapParentAt(new PlanetTile(i, ocean)) != null) { continue; }
                    surface.GetTileNeighbors(new PlanetTile(i, surface), cnb);
                    if (!cnb.Any(n => !n.Tile.WaterCovered)) { continue; }
                    coastN++;
                    coastSurfaceSum += -st.elevation;
                    float want = Mathf.Max(0f, -st.elevation), got = -ot.elevation;
                    WaterMapExtension ext = ot.PrimaryBiome.GetModExtension<WaterMapExtension>();
                    if (Mathf.Abs(want - got) < 0.5f) { coastSame++; }
                    else if (ext != null && (Mathf.Abs(got - ext.seafloorDepth.min) < 0.5f || Mathf.Abs(got - ext.seafloorDepth.max) < 0.5f)) { coastClamped++; }
                }
                ctx.Note($"coastal tiles: {coastSame}/{coastN} match the surface depth, {coastClamped} held at their biome's depth limit; average surface depth there {(coastN > 0 ? coastSurfaceSum / coastN : 0f):0} m");
                ctx.Check(coastN > 0 && coastSame + coastClamped == coastN, "next to land the ocean depth matches the surface view (or its biome's depth limit)");
            }
            // 水溫（2026-11-11）：依深度與正上方氣溫；淺水有季節變化、深海沒有；地下冰洋固定在冰點
            float AvgTemp(string def) { var ts = ocean.Tiles.Where(t => t.PrimaryBiome.defName == def).ToList(); return ts.Count == 0 ? float.NaN : ts.Average(t => t.temperature); }
            float reefT = AvgTemp("DepthRim_ShallowReef"), kelpT = AvgTemp("DepthRim_KelpForest"), deepReefT = AvgTemp("DepthRim_DeepReef"), plainT = AvgTemp("DepthRim_SeabedPlain"),
                  trenchT = AvgTemp("DepthRim_AbyssalTrench"), ventT = AvgTemp("DepthRim_HydrothermalField"), iceT = AvgTemp("DepthRim_PolarIceCap");
            ctx.Note($"average water temperature: reef {reefT:0.0}, kelp {kelpT:0.0}, deep reef {deepReefT:0.0}, plain {plainT:0.0}, trench {trenchT:0.0}, vents {ventT:0.0}, subglacial {iceT:0.0} °C");
            ctx.Check(reefT > kelpT && kelpT > plainT && deepReefT > plainT, "shallow water is warmer than the deep sea; tropical reefs are the warmest");
            ctx.Check(plainT > 0.4f && plainT < 3.5f && (float.IsNaN(trenchT) || trenchT < 3.5f), "the abyssal plain and trenches are 1–3 °C");
            ctx.Check(float.IsNaN(ventT) || ventT > plainT + 4f, "hydrothermal fields are warmer than the abyssal plain");
            var subTiles = ocean.Tiles.Where(t => OceanBiomeSelector.IsSubglacial(t.PrimaryBiome.defName)).ToList();
            ctx.Check(subTiles.All(t => t.temperature >= OceanTemperature.FreezingPoint - 0.01f && t.temperature <= OceanTemperature.SubglacialDeepWater + 0.01f)
                      && (float.IsNaN(iceT) || iceT < -1.2f), "the subglacial ocean is near freezing (shallow at the freezing point, deep water about -0.5 °C)");
            // 地下冰洋分深淺層（2026-11-11）：每一層的格子深度落在該層，水深層級跟著層
            var subByLayer = subTiles.GroupBy(t => t.PrimaryBiome.defName).OrderBy(g => g.Key).ToList();
            ctx.Note("subglacial layers: " + string.Join(", ", subByLayer.Select(g => $"{g.Key} {g.Count()} ({g.Min(t => -t.elevation):0}–{g.Max(t => -t.elevation):0} m, tier {g.First().PrimaryBiome.GetModExtension<WaterMapExtension>().depthTier})")));
            ctx.Check(subTiles.All(t => OceanBiomeSelector.DefName(OceanBiomeSelector.SubglacialFor(-t.elevation)) == t.PrimaryBiome.defName || t.PrimaryBiome.defName == "DepthRim_SubglacialThermocline" && -t.elevation >= 2000f),
                      "each subglacial tile is in the layer that matches its depth");
            ctx.Check(subTiles.Count == 0 || subByLayer.Count >= 2, "the subglacial ocean has more than one depth layer");
            float YearRange(PlanetTile t)
            {
                var temps = Enumerable.Range(0, 12).Select(m => Find.World.tileTemperatures.OutdoorTemperatureAt(t, 1 + m * 300000)).ToList();
                return temps.Max() - temps.Min();
            }
            PlanetTile FirstOf(string def, System.Func<int, bool> extra = null)
            {
                for (int i = 0; i < ocean.TilesCount; i++)
                {
                    if (ocean[i].PrimaryBiome.defName == def && (extra == null || extra(i))) { return new PlanetTile(i, ocean); }
                }
                return PlanetTile.Invalid;
            }
            // 中高緯度的淺水（海藻林）季節最明顯；赤道附近季節本來就小
            PlanetTile kelpTile = FirstOf("DepthRim_KelpForest", i => Find.WorldGrid.DistanceFromEquatorNormalized(new PlanetTile(i, ocean)) > 0.4f);
            PlanetTile plainTile = FirstOf("DepthRim_SeabedPlain"), iceTile = FirstOf("DepthRim_PolarIceCap");
            float kelpRange = kelpTile.Valid ? YearRange(kelpTile) : float.NaN, plainRange = plainTile.Valid ? YearRange(plainTile) : float.NaN, iceRange = iceTile.Valid ? YearRange(iceTile) : float.NaN;
            ctx.Note($"yearly range: kelp {kelpRange:0.00} °C (lat {(kelpTile.Valid ? Find.WorldGrid.LongLatOf(kelpTile).y : 0f):0}), abyssal plain {plainRange:0.00} °C, subglacial {iceRange:0.00} °C");
            ctx.Check(!kelpTile.Valid || kelpRange > 2f, "shallow kelp forests warm and cool with the seasons");
            ctx.Check(plainTile.Valid && plainRange < 0.5f, "the abyssal plain stays the same temperature all year");
            ctx.Check(!iceTile.Valid || iceRange < 0.01f, "the subglacial ocean stays the same temperature all year");
            // 冰原（陸上的冰）算陸地，下方是陸塊（抽樣）；地下冰洋只在冰海（海冰）下（2026-11-11）
            int iceChecked = 0, iceOk = 0;
            for (int i = 0; i < surface.TilesCount && iceChecked < 40; i += 97)
            {
                Tile st = surface[i];
                if (st.PrimaryBiome != BiomeDefOf.IceSheet) { continue; }
                iceChecked++;
                PlanetTile under = OceanWorldView.TileBelow(new PlanetTile(i, surface), ocean);
                if (under.Valid && OceanWorldView.IsLandmass(under.Tile.PrimaryBiome)) { iceOk++; }
            }
            ctx.Note($"ice sheet samples: {iceOk}/{iceChecked} have landmass below");
            ctx.Check(iceChecked == 0 || iceOk == iceChecked, "the ice sheet counts as land (landmass below)");
            int subglacial = 0, subglacialUnderSeaIce = 0, seaIce = 0, seaIceOverSubglacial = 0;
            bool sameGrid = surface.TilesCount == ocean.TilesCount;
            // 測試夾具會把殖民地正下方那一格改成地下冰洋（DepthRimFixture.EnsureOcean），不算
            var homeTiles = new HashSet<int>(Find.Maps.Where(m => m.Tile.Layer == surface).Select(m => m.Tile.tileId));
            for (int i = 0; sameGrid && i < ocean.TilesCount; i++)
            {
                if (homeTiles.Contains(i) || Find.WorldObjects.MapParentAt(new PlanetTile(i, ocean)) != null) { continue; }
                bool sub = OceanBiomeSelector.IsSubglacial(ocean[i].PrimaryBiome.defName), ice = surface[i].PrimaryBiome == BiomeDefOf.SeaIce;
                if (sub) { subglacial++; if (ice) { subglacialUnderSeaIce++; } }
                if (ice) { seaIce++; if (sub) { seaIceOverSubglacial++; } }
            }
            ctx.Note($"subglacial ocean tiles under sea ice: {subglacialUnderSeaIce}/{subglacial}; sea-ice tiles over subglacial ocean: {seaIceOverSubglacial}/{seaIce}");
            ctx.Check(subglacialUnderSeaIce == subglacial, "the subglacial ocean only forms under sea ice");
            // 冰海下必定是地下冰洋（2026-11-11 使用者定案）
            ctx.Check(seaIceOverSubglacial == seaIce, "every sea-ice tile has subglacial ocean below");
            ctx.Check(surface.zoomInToLayer == ocean, "zooming in on the surface goes to the ice ocean");
            ctx.Check(ocean.zoomOutToLayer == surface, "zooming out of the ice ocean goes to the surface");
            ctx.Check(!surface.HasConnectionFromTo(ocean) && !ocean.HasConnectionFromTo(surface), "no gravship/shuttle connection between surface and ocean");
            ctx.Check(Find.WorldGrid.PlanetLayers.Values.Count(l => l.Def == DepthRimDefOf.DepthRim_Ocean) == 1, "exactly one ice ocean layer");
            ctx.Check(DMSE.TransferEnvironment.AllowsTransfer(DepthRimDefOf.DepthRim_Ocean), "DMSE transfer flight (the submarine) is enabled on the ocean");
            int seabed = 0;
            for (int i = 0; i < ocean.TilesCount; i += 7)
            {
                if (DepthTierUtility.MaxTierAt(new PlanetTile(i, ocean)) == DepthTier.Seabed) { seabed++; }
            }
            ctx.Note($"sampled seabed-tier tiles: {seabed} of {ocean.TilesCount / 7 + 1}");
            yield break;
        }

        /// <summary>世界地圖上的海洋地形：生態區貼圖、海床深度、只在選到海洋層時畫、看海洋時地表藏起來。</summary>
        /// <summary>
        /// 海洋格的海床深度、年均水溫、地勢要跟著存檔走（原版 PlanetLayer 只存生態區，2026-11-21 前讀檔後深度都是 0 m），
        /// 沒有這些資料的舊存檔要能依世界種子重算回世界生成時的深度。
        /// </summary>
        public static IEnumerable<Wait> L3_OceanTileDataSurvivesSave(TestContext ctx)
        {
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            ctx.Require(ocean != null && ocean.Tiles.Count == ocean.TilesCount, "ocean layer has tiles");
            int n = ocean.TilesCount;
            float[] elev = new float[n], temp = new float[n];
            Hilliness[] hills = new Hilliness[n];
            bool[] water = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Tile t = ocean[i];
                elev[i] = t.elevation; temp[i] = t.temperature; hills[i] = t.hilliness;
                water[i] = !OceanWorldView.IsLandmass(t.PrimaryBiome);
            }
            int waterCount = water.Count(w => w);
            ctx.Check(waterCount > 0 && Enumerable.Range(0, n).All(i => !water[i] || elev[i] < 0f), $"every ocean tile has a seafloor below sea level ({waterCount} ocean tiles)");

            // ── 舊存檔：格子資料遺失（高度 = 圖層半徑）後依種子重算 ──
            for (int i = 0; i < n; i++) { ocean[i].elevation = ocean.Radius; ocean[i].temperature = 0f; }
            int restored = WorldGenStep_OceanTiles.RestoreTileData(Find.World.info.seedString, ocean);
            int depthMiss = Enumerable.Range(0, n).Count(i => Mathf.Abs(ocean[i].elevation - elev[i]) > 0.01f);
            int tempMiss = Enumerable.Range(0, n).Count(i => Mathf.Abs(ocean[i].temperature - temp[i]) > 0.01f);
            int firstMiss = Enumerable.Range(0, n).FirstOrDefault(i => Mathf.Abs(ocean[i].elevation - elev[i]) > 0.01f);
            ctx.Check(restored == n, $"restore filled every tile ({restored}/{n})");
            ctx.Check(depthMiss == 0, $"restored depths match world generation ({depthMiss} differ{(depthMiss > 0 ? $", e.g. #{firstMiss} {ocean[firstMiss].elevation:0.0} vs {elev[firstMiss]:0.0}" : "")})");
            ctx.Check(tempMiss == 0, $"restored water temperatures match world generation ({tempMiss} differ)");
            ctx.Check(Enumerable.Range(0, n).All(i => ocean[i].hilliness != Hilliness.Undefined), "every restored tile has a hilliness");
            for (int i = 0; i < n; i++) { ocean[i].elevation = elev[i]; ocean[i].temperature = temp[i]; ocean[i].hilliness = hills[i]; }

            // ── 存檔再讀檔 ──
            Game before = Current.Game;
            GameDataSaveLoader.SaveGame(DepthRimFixture.TestSaveName);
            LongEventHandler.QueueLongEvent(() => SavedGameLoaderNow.LoadGameFromSaveFileNow(DepthRimFixture.TestSaveName), "LoadingLongEvent", false, null);
            yield return new WaitRealtime(() => Current.Game != before && Current.ProgramState == ProgramState.Playing && !LongEventHandler.AnyEventNowOrWaiting, 120f);
            ctx.Require(Current.Game != before, "the game reloaded");
            yield return new WaitFrames(10);
            ocean = DepthRimFixture.OceanLayer;
            ctx.Require(ocean != null && ocean.Tiles.Count == n, "the reloaded ocean layer has the same tiles");
            int elevBad = Enumerable.Range(0, n).Count(i => Mathf.Abs(ocean[i].elevation - elev[i]) > 0.051f);
            int tempBad = Enumerable.Range(0, n).Count(i => Mathf.Abs(ocean[i].temperature - temp[i]) > 0.006f);
            int hillBad = Enumerable.Range(0, n).Count(i => ocean[i].hilliness != hills[i]);
            ctx.Check(elevBad == 0, $"after loading, seafloor depths are unchanged ({elevBad} differ)");
            ctx.Check(tempBad == 0, $"after loading, water temperatures are unchanged ({tempBad} differ)");
            ctx.Check(hillBad == 0, $"after loading, hilliness is unchanged ({hillBad} differ)");
            int sample = Enumerable.Range(0, n).First(i => water[i]);
            ctx.Check(OceanTemperature.TryGetWater(new PlanetTile(sample, ocean), out _, out float depth, out _) && depth > 0f,
                $"after loading, an ocean tile reports its depth ({depth:0} m)");
            ctx.Check(Patch_PlanetLayer_ExposeOceanTiles.PendingRestore == null, "a save with ocean tile data needs no restore");
        }

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
            // 選取：滑鼠點海洋格要拿到海洋層的格子，世界檢視面板要有分頁
            Find.WorldCameraDriver.JumpTo(home);
            yield return new WaitFrames(10);
            UnityEngine.Vector3 screen = Find.WorldCamera.WorldToScreenPoint(ocean.GetTileCenter(home));
            UnityEngine.Vector2 uiPos = new UnityEngine.Vector2(screen.x, screen.y) / Prefs.UIScale;
            WorldTerrainColliderManager.EnsureRaycastCollidersUpdated();
            UnityEngine.Ray ray = Find.WorldCamera.ScreenPointToRay(uiPos * Prefs.UIScale);
            bool hit = UnityEngine.Physics.Raycast(ray, out UnityEngine.RaycastHit hitInfo, 1500f, WorldCameraManager.WorldLayerMask);
            PlanetTile picked = GenWorld.TileAt(uiPos);
            ctx.Note($"pick at {uiPos}: physics hit {hit} ({(hit ? hitInfo.collider?.gameObject.name : "-")}), tile {picked} layer {picked.LayerDef?.defName}, raycastable ocean {ocean.Raycastable} surface {surface.Raycastable}, ocean colliders {ocean.WorldDrawLayers.Count(l => l.Raycastable)}");
            ctx.Check(picked.Valid && picked.Layer == ocean, $"clicking the seabed picks an ocean tile ({picked})");
            Find.WorldSelector.ClearSelection();
            Find.WorldSelector.SelectedTile = home;
            yield return new WaitFrames(5);
            List<InspectTabBase> tabs = (ocean.Def.Tabs ?? new List<WITab>()).Cast<InspectTabBase>().ToList();
            ctx.Note($"selected {Find.WorldSelector.SelectedTile}, any selected {Find.WorldSelector.AnyObjectOrTileSelected}, tabs visible: {string.Join(", ", tabs.Select(t => t.GetType().Name + "=" + t.IsVisible))}");
            ctx.Check(Find.WorldSelector.SelectedTile == home && tabs.Any(t => t.IsVisible), "a selected ocean tile shows inspector tabs");
            ctx.Check(tabs.Any(t => t is WITab_OceanTerrain && t.IsVisible), "the inspector has the ocean terrain tab for an ocean tile");
            Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
            WITab_OceanTerrain terrainTab = tabs.OfType<WITab_OceanTerrain>().FirstOrDefault();
            if (terrainTab != null) { InspectPaneUtility.OpenTab(terrainTab.GetType()); }
            yield return new WaitFrames(10);
            ctx.Screenshot("ocean_inspector");
            yield return new WaitFrames(10);
            // 看海洋層時要有「檢視地表」按鈕（地表的原版按鈕需要圖層連線，海洋層沒有）
            List<Command_Action> layerGizmos = Find.WorldGrid.GetGizmos().OfType<Command_Action>()
                .Where(g => g.icon == surface.Def.ViewGizmoTexture).ToList();
            ctx.Check(layerGizmos.Count == 1, $"the ocean view has exactly one 'view surface' gizmo ({layerGizmos.Count})");
            if (layerGizmos.Count > 0)
            {
                layerGizmos[0].action();
                yield return new WaitFrames(5);
                ctx.Check(PlanetLayer.Selected == surface, "the 'view surface' gizmo switches back to the surface");
                ctx.Check(OceanWorldView.ReturnToSurfaceGizmo() == null, "no extra surface gizmo while viewing the surface");
                PlanetLayer.Selected = ocean;
                yield return new WaitFrames(5);
                Find.WorldSelector.SelectedTile = home;
            }
            // 在海洋視圖點陸塊格：自動切到地表、選正上方的地表格；點海洋格不動
            // 殖民地在陸地上，正下方（home）是陸塊，所以另外找一格真正的海洋格
            PlanetTile landmass = PlanetTile.Invalid, water = PlanetTile.Invalid;
            for (int i = 0; i < ocean.TilesCount && (!landmass.Valid || !water.Valid); i++)
            {
                if (OceanWorldView.IsLandmass(ocean[i].PrimaryBiome)) { if (!landmass.Valid) { landmass = new PlanetTile(i, ocean); } }
                else if (!water.Valid) { water = new PlanetTile(i, ocean); }
            }
            ctx.Check(water.Valid && !OceanWorldView.TryJumpToSurface(water) && PlanetLayer.Selected == ocean, $"an ocean tile does not jump to the surface ({water})");
            if (landmass.Valid)
            {
                Find.WorldSelector.SelectedTile = landmass;
                bool jumped = OceanWorldView.TryJumpToSurface(landmass);
                yield return new WaitFrames(5);
                PlanetTile sel = Find.WorldSelector.SelectedTile;
                ctx.Note($"landmass {landmass} -> selected {sel} layer {sel.LayerDef?.defName}");
                ctx.Check(jumped && PlanetLayer.Selected == surface && sel.Layer == surface && !sel.Tile.WaterCovered
                          && Vector3.Dot(ocean.GetTileCenter(landmass).normalized, surface.GetTileCenter(sel).normalized) > 0.999f,
                          "clicking a landmass in the ocean view jumps to the surface tile above it");
                PlanetLayer.Selected = ocean;
                yield return new WaitFrames(5);
                Find.WorldSelector.SelectedTile = home;
            }
            else
            {
                ctx.Check(false, "found a landmass tile on the ocean layer");
            }
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
            // 對準地下冰洋（冰海下）與一般海洋的交界
            PlanetTile iceEdge = PlanetTile.Invalid;
            var edgeNb = new List<PlanetTile>();
            for (int i = 0; i < ocean.TilesCount && !iceEdge.Valid; i++)
            {
                if (!OceanBiomeSelector.IsSubglacial(ocean[i].PrimaryBiome.defName) || !surface[i].WaterCovered) { continue; }
                ocean.GetTileNeighbors(new PlanetTile(i, ocean), edgeNb);
                if (edgeNb.Count(n => n.Tile.elevation < -2500f) >= 1) { iceEdge = new PlanetTile(i, ocean); }
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
            // 冰下非常低光（2026-11-11）
            ctx.Check(ocean.weatherManager.curWeather?.defName == "DepthRim_UnderIce", $"the subglacial map has the under-ice weather ({ocean.weatherManager.curWeather?.defName})");
            ctx.Check(ocean.skyManager.CurSkyGlow <= 0.061f, $"daylight under the ice is very dim (sky glow {ocean.skyManager.CurSkyGlow:0.000})");
            WaterTierMaterials.Outer(DepthTier.Shallow);
            WaterTierMaterials.UpdateSkyLight(ocean);
            UnityEngine.Material shallowMat = WaterTierMaterials.Outer(DepthTier.Shallow);
            ctx.Check(shallowMat.GetFloat("_CausticStrength") == 0f && shallowMat.GetFloat("_LightShaftStrength") == 0f, "no sunlight caustics or light shafts under the ice");
            ctx.Check(System.Math.Abs(ocean.mapTemperature.OutdoorTemp - OceanTemperature.FreezingPoint) < 0.05f, $"the subglacial ocean map's water is at the freezing point ({ocean.mapTemperature.OutdoorTemp:0.00} °C)");
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
