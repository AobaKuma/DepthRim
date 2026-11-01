using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>海洋圖層（取代冰洋）：七種生態區各自生成地圖（V16）、抗壓水密牆與升降井的生態區限制（V17）。</summary>
    public static class Tests_Ocean
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        private static readonly IntVec3 MapSize = new IntVec3(100, 1, 100);

        /// <summary>
        /// V16：每種生態區在海洋層找一格（這個世界沒有就借一格空的改生態區，測完改回），真的生成一張 100×100 的地圖，
        /// 檢查水深層級、水位系統、岩石種類（珊瑚只出現在珊瑚礁）、海藻、熱液泉、深淵比例；拍一張全景；最後移除地圖。
        /// </summary>
        public static IEnumerable<Wait> V16_OceanBiomeMaps(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            ctx.Require(ocean != null, "ocean layer exists");
            foreach (OceanBiome kind in System.Enum.GetValues(typeof(OceanBiome)))
            {
                BiomeDef biome = DefDatabase<BiomeDef>.GetNamedSilentFail(OceanBiomeSelector.DefName(kind));
                if (!ctx.Check(biome != null, $"{kind}: BiomeDef loaded")) { continue; }
                WaterMapExtension ext = biome.GetModExtension<WaterMapExtension>();
                PlanetTile tile = FreeTileOf(ocean, biome, out BiomeDef original);
                if (!ctx.Check(tile.Valid, $"{kind}: found a free ocean tile")) { continue; }

                MapParent site = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
                site.Tile = tile;
                site.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(site);
                Map map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, MapSize, DepthRimDefOf.DepthRim_OceanSite);
                if (!ctx.Check(map != null, $"{kind}: map generated"))
                {
                    site.Destroy();
                    continue;
                }
                // 海洋地點在沒有玩家建築時會自己移除地圖（水下基地的保留規則），所以先放一段玩家的電線把它留住，測完再手動移除
                Thing anchor = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.PowerConduit), map.Center, map, WipeMode.Vanish);
                anchor.SetFaction(Faction.OfPlayer);
                yield return new WaitFrames(2);
                ctx.Require(site.HasMap && site.Map == map, $"{kind}: the map stays while a player building is on it");

                ctx.Check(map.Biome == biome, $"{kind}: map uses {biome.defName}");
                ctx.Check(FloodingUtility.DepthTierOf(map) == ext.depthTier, $"{kind}: map depth tier is the biome's ({ext.depthTier})");
                ctx.Check(map.Flooding()?.Active == true, $"{kind}: the flooding system is active");

                // 天然岩體會壓縮存檔、熱液泉不進 listerThings，所以直接掃格子
                List<Thing> all = map.AllCells.SelectMany(c => map.thingGrid.ThingsListAtFast(c)).Distinct().ToList();
                List<ThingDef> rocks = all.Where(t => t.def.building != null && t.def.building.isNaturalRock && !t.def.building.isResourceRock)
                    .Select(t => t.def).Distinct().ToList();
                ctx.Note($"{kind}: rocks {string.Join(", ", rocks.Select(r => r.defName))}");
                ctx.Check(rocks.All(r => biome.forceRockTypes.Contains(r) || r.IsSmoothed || r == ThingDefOf.CollapsedRocks), $"{kind}: only the biome's rock types appear");
                bool coral = rocks.Any(r => r.defName.Contains("Coral"));
                bool reef = kind == OceanBiome.ShallowReef || kind == OceanBiome.DeepReef;
                ctx.Check(coral == reef, $"{kind}: coral rock {(reef ? "appears" : "does not appear")}");

                int kelp = all.Count(t => t.def.defName == "DepthRim_Kelp");
                int silt = map.AllCells.Count(c => c.GetTerrain(map).defName == "DepthRim_Seabed_Silt");
                if (kind == OceanBiome.KelpForest) { ctx.Check(kelp > 20, $"{kind}: kelp grows ({kelp} plants on {silt} silt cells)"); }
                else if (kind != OceanBiome.ShallowReef) { ctx.Check(kelp == 0, $"{kind}: no kelp ({kelp})"); }

                int vents = all.Count(t => t.def == ThingDefOf.SteamGeyser);
                ctx.Check(kind == OceanBiome.HydrothermalField ? vents >= 1 : vents == 0, $"{kind}: hydrothermal vents {vents}");

                int abyss = map.AllCells.Count(c => c.GetTerrain(map) == DepthRimDefOf.DepthRim_Abyss);
                float abyssFraction = abyss / (float)map.cellIndices.NumGridCells;
                ctx.Note($"{kind}: abyss {abyssFraction:P1}");
                if (!FloodingMath.CarvesAbyss(ext.depthTier)) { ctx.Check(abyss == 0, $"{kind}: shallow biomes have no abyss"); }
                if (kind == OceanBiome.AbyssalTrench) { ctx.Check(abyssFraction > 0.08f, $"{kind}: the trench is mostly abyss ({abyssFraction:P1})"); }

                if (kind != OceanBiome.PolarIceCap && kind != OceanBiome.SeabedPlain)
                {
                    // 測試地圖沒有出口可以揭開迷霧：截圖前全部揭開，鏡頭對準這個生態區的特色（珊瑚、海藻、熱液泉、深淵）
                    map.fogGrid.ClearAllFog();
                    Thing feature = all.FirstOrDefault(t => t.def.defName.Contains("Coral") || t.def.defName == "DepthRim_Kelp" || t.def == ThingDefOf.SteamGeyser);
                    IntVec3 focus = feature?.Position ?? map.AllCells.FirstOrDefault(c => c.GetTerrain(map) == DepthRimDefOf.DepthRim_Abyss);
                    if (!focus.IsValid || focus == IntVec3.Zero) { focus = map.Center; }
                    foreach (Wait w in ctx.ScreenshotOn(map, focus, 16f, "biome_" + kind, 30)) { yield return w; }
                    yield return new WaitFrames(20);
                }

                Current.Game.CurrentMap = DepthRimFixture.Home;
                Current.Game.DeinitAndRemoveMap(map, false);
                if (!site.Destroyed) { site.Destroy(); }
                if (original != null) { tile.Tile.PrimaryBiome = original; }
                yield return new WaitFrames(5);
            }
        }

        /// <summary>這個生態區沒有世界物件的格子；世界上沒有這種生態區時借一格空的海床平原（回傳原本的生態區，測完改回）。</summary>
        private static PlanetTile FreeTileOf(PlanetLayer ocean, BiomeDef biome, out BiomeDef original)
        {
            original = null;
            for (int i = 0; i < ocean.TilesCount; i++)
            {
                PlanetTile t = new PlanetTile(i, ocean);
                if (t.Tile.PrimaryBiome == biome && Find.WorldObjects.MapParentAt(t) == null) { return t; }
            }
            for (int i = ocean.TilesCount / 3; i < ocean.TilesCount; i++)
            {
                PlanetTile t = new PlanetTile(i, ocean);
                if (Find.WorldObjects.MapParentAt(t) == null)
                {
                    original = t.Tile.PrimaryBiome;
                    t.Tile.PrimaryBiome = biome;
                    return t;
                }
            }
            return PlanetTile.Invalid;
        }

        /// <summary>
        /// V17：抗壓水密牆圍的艙室是密閉的、受損時滲水只有一般牆公式的 1/10；
        /// 升降井的放置限制：正下方是淺海生態區可以蓋，換成深海生態區就不行（測完改回）。
        /// </summary>
        public static IEnumerable<Wait> V17_HullWallAndShaftLimit(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            ThingDef hull = ThingDef.Named("DepthRim_PressureHullWall");
            ctx.Check(hull.graphic != null && hull.graphic != BaseContent.BadGraphic, "hull wall graphic loads (Thing/ShipHull)");
            ctx.Check(ThingDef.Named("DepthRim_CoralRock").graphic != BaseContent.BadGraphic && ThingDef.Named("DepthRim_DarkCoralRock").graphic != BaseContent.BadGraphic,
                "coral rock graphics load (Thing/coralWall, Thing/coralWallDark)");
            ctx.Check(LeakResistanceExtension.FactorFor(hull) == 0.1f && LeakResistanceExtension.FactorFor(DepthRimDefOf.DepthRim_WatertightDoor) == Building_WatertightDoor.LeakFactor,
                "leak factors come from the Defs (hull 0.1, watertight door 0.25)");

            CellRect ring = DepthRimFixture.ClearOceanArea(7);
            List<Thing> walls = ring.EdgeCells.Select(c => DepthRimFixture.Spawn(hull, c, map)).ToList();
            CellRect inner = ring.ContractedBy(1);
            Room room = inner.CenterCell.GetRoom(map);
            ctx.Require(room != null && !flooding.IsOpenWater(room) && room.CellCount == inner.Area, "hull walls make a sealed compartment");
            byte[] arr = HarmonyLib.Traverse.Create(flooding).Field("levels").GetValue<byte[]>();
            foreach (IntVec3 c in room.Cells) { arr[map.cellIndices.CellToIndex(c)] = 0; }
            Thing damaged = walls.First(w => w.Position.z == ring.minZ && w.Position.x == inner.CenterCell.x);
            damaged.HitPoints = Mathf.Max(1, damaged.MaxHitPoints / 10);
            yield return Updates(1);
            foreach (IntVec3 c in room.Cells) { arr[map.cellIndices.CellToIndex(c)] = 0; }
            yield return Updates(1);
            float level = flooding.LevelAt(inner.CenterCell);
            float plain = FloodingMath.LeakVolume(damaged.HitPoints, damaged.MaxHitPoints, flooding.DepthTier, 1f, 0f, MapComponent_Flooding.UpdateInterval / 60f) / room.CellCount;
            ctx.Check(flooding.InflowCells.Contains(damaged.Position), "the damaged hull wall still leaks a little");
            ctx.Check(level > 0f && level <= plain * 0.1f * 2.5f + 0.005f, $"one update of leaking is about a tenth of an ordinary wall ({level:0.0000} vs ordinary {plain:0.0000})");
            foreach (Wait w in ctx.ScreenshotOn(map, inner.CenterCell, 9f, "pressure_hull_wall", 30)) { yield return w; }
            yield return new WaitFrames(20);

            // 升降井：淺海（極地冰蓋，固定在測試世界）可以蓋；把正下方換成海床平原（深海）就不行
            Map home = DepthRimFixture.Home;
            PlanetTile below = DepthRimFixture.OceanLayer.GetClosestTile_NewTemp(home.Tile);
            ctx.Require(below.Valid, "found the ocean tile below the colony");
            PlaceWorker_IceShaft worker = new PlaceWorker_IceShaft();
            IntVec3 spot = DepthRimFixture.Pad.CenterCell;
            ctx.Check(PlaceWorker_IceShaft.OceanBelow(home) == below.Tile.PrimaryBiome, "the place worker looks at the tile right below");
            ctx.Check(worker.AllowsPlacing(DepthRimFixture.IceShaft, spot, Rot4.North, home).Accepted, "shaft allowed over the polar ice cap (shallow)");
            BiomeDef original = below.Tile.PrimaryBiome;
            below.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("DepthRim_SeabedPlain");
            AcceptanceReport deep = worker.AllowsPlacing(DepthRimFixture.IceShaft, spot, Rot4.North, home);
            ctx.Check(!deep.Accepted && !deep.Reason.NullOrEmpty(), $"shaft refused over a seabed plain (deep): {deep.Reason}");
            below.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("DepthRim_DeepReef");
            ctx.Check(worker.AllowsPlacing(DepthRimFixture.IceShaft, spot, Rot4.North, home).Accepted, "shaft allowed over a deep coral reef (thermocline)");
            below.Tile.PrimaryBiome = original;
        }
    }
}
