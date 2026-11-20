using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>海岸造船 M1（Docs/海岸船舶與潛水鐘_機制設計.md §3、§11）：在原版海岸地圖的灣裡造船、下水到隔壁的海面。</summary>
    public static class Tests_Coastal
    {
        /// <summary>
        /// C1：在地表殖民地上挖一片原版深海水當海灣，鋪 9×9 船體平台，蓋龍骨、艦橋、水面推進器、電池，放一個殖民者與貨物；
        /// 檢查：龍骨在原版地圖上放得下；艦橋只給「下水」；沒電、舷梯沒收、旁邊沒有海都會擋；
        /// 把殖民地旁邊一格設成開放海面後下水：整具船體連人帶貨搬到那一格的海面地點，灣裡還原成原本的深海水；
        /// 浮在海面上的船沒有潛航器建造不能下潛。最後還原地形、世界格與研究。
        /// </summary>
        public static IEnumerable<Wait> C1_CoastalLaunch(TestContext ctx)
        {
            Map home = DepthRimFixture.Home;
            yield return new WaitFrames(2);
            TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            TerrainDef deep = TerrainDefOf.WaterOceanDeep;
            ResearchProjectDef coastal = DepthRimDefOf.DepthRim_CoastalShipbuilding;
            ctx.Require(coastal != null, "coastal shipbuilding research exists");
            ctx.Check(platform.researchPrerequisites.Contains(coastal) && ThingDef.Named("DepthRim_Keel").researchPrerequisites.Contains(coastal)
                && ThingDef.Named("DepthRim_Helm").researchPrerequisites.Contains(coastal) && DepthRimDefOf.DepthRim_SurfaceThruster.researchPrerequisites.Contains(coastal),
                "platform, keel, helm and surface thruster are unlocked by coastal shipbuilding");
            ctx.Check(coastal.knowledgeCategory == null && coastal.baseCost > 0f, "coastal shipbuilding is ordinary research");
            ctx.Check(DepthRimDefOf.DepthRim_SurfaceThruster.GetCompProperties<CompProperties_TransferEngine>() == null
                && DepthRimDefOf.DepthRim_SurfaceThruster.GetCompProperties<CompProperties_SurfaceThruster>() != null, "the surface thruster is not a DMSE transfer engine");
            ctx.Check(CoastalLaunch.IsShipyardMap(home), "the home map is a shipyard map");

            // ── 海灣：一片原版深海水 ──
            bool Free(IntVec3 c) => c.InBounds(home) && !c.Fogged(home) && map_NoBlockers(home, c) && home.terrainGrid.UnderTerrainAt(c) == null
                && home.terrainGrid.TerrainAt(c) is TerrainDef t && !t.IsWater && !MapComponent_SeabedMemory.IsPlatform(t);
            CellRect deck = CellRect.Empty;
            foreach (IntVec3 c in home.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 9, 9);
                if (r.minX >= 15 && r.minZ >= 15 && r.maxX < home.Size.x - 15 && r.maxZ < home.Size.z - 15 && r.ExpandedBy(2).Cells.All(Free))
                {
                    deck = r;
                    break;
                }
            }
            ctx.Require(deck.Area > 0, "found a clear 13x13 patch on the home map");
            CellRect bay = deck.ExpandedBy(2);
            Dictionary<IntVec3, TerrainDef> original = bay.Cells.ToDictionary(c => c, c => home.terrainGrid.TerrainAt(c));
            foreach (IntVec3 c in bay)
            {
                foreach (Thing t in c.GetThingList(home).Where(t => t is Plant || t.def.category == ThingCategory.Filth).ToList()) { t.Destroy(); }
                home.terrainGrid.SetTerrain(c, deep);
            }
            foreach (IntVec3 c in deck)
            {
                home.terrainGrid.SetTerrain(c, platform);
            }
            ctx.Note($"bay {bay}, deck {deck}");

            // ── 船 ──
            IntVec3 center = deck.CenterCell;
            ThingDef keelDef = ThingDef.Named("DepthRim_Keel");
            AcceptanceReport keelOk = GenConstruct.CanPlaceBlueprintAt(keelDef, center, Rot4.North, home);
            ctx.Check(keelOk.Accepted, $"a keel frame can be placed on a hull platform on a vanilla map ({keelOk.Reason})");
            AcceptanceReport thrOk = GenConstruct.CanPlaceBlueprintAt(DepthRimDefOf.DepthRim_SurfaceThruster, center + new IntVec3(3, 0, -2), Rot4.North, home);
            ctx.Check(thrOk.Accepted, $"a surface thruster can be placed on the deck ({thrOk.Reason})");
            foreach (IntVec3 c in deck)
            {
                Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                conduit.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(conduit, c, home);
            }
            List<Thing> built = new List<Thing>();
            Thing Build(ThingDef def, IntVec3 at)
            {
                Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                t.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(t, at, home, Rot4.North);
                built.Add(t);
                return t;
            }
            Thing keel = Build(keelDef, center);
            Thing helm = Build(ThingDef.Named("DepthRim_Helm"), center + new IntVec3(-2, 0, 3));
            Thing thruster = Build(DepthRimDefOf.DepthRim_SurfaceThruster, center + new IntVec3(3, 0, -2));
            Thing battery = Build(ThingDef.Named("Battery"), center + new IntVec3(-3, 0, -3));
            Thing cargo = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), center + new IntVec3(1, 0, -3), home);
            cargo.stackCount = 23;
            Pawn sailor = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(sailor, center + new IntVec3(0, 0, -2), home);
            if (sailor.drafter != null) { sailor.drafter.Drafted = true; } // 別在下水前走下甲板
            MapComponent_PileNetwork.For(home).MarkDirty();
            yield return new WaitTicks(5);

            CompPowerTrader helmPower = helm.TryGetComp<CompPowerTrader>();
            ctx.Check(helmPower != null, "the helm uses power");
            string noPower = CoastalLaunch.BlockReason(helm, out PileNetwork net, out _);
            ctx.Check(net != null && net.Footprint.Count == deck.Area, $"the keel holds the whole deck as one hull ({net?.Footprint.Count}/{deck.Area})");
            ctx.Check(helmPower.PowerOn || noPower == "DepthRim.Launch.NoPower".Translate().ToString(), $"without power the launch is blocked ({noPower})");
            battery.TryGetComp<CompPowerBattery>().AddEnergy(5000f);
            yield return new WaitUntil(() => helmPower.PowerOn, 1200);
            ctx.Check(helmPower.PowerOn, "the helm is powered by the battery");

            List<Gizmo> gizmos = helm.GetGizmos().ToList();
            ctx.Check(gizmos.OfType<Command_Action>().Any(g => g.defaultLabel == "DepthRim.Launch.Label".Translate().ToString()), "the helm in port offers Launch");
            ctx.Check(!gizmos.OfType<Command_Action>().Any(g => g.icon == SubmarineNavigation.NavigateIcon && g.defaultLabel != "DepthRim.Launch.Label".Translate().ToString()),
                "and no Navigate command (sailing starts at sea)");

            // ── 旁邊的海 ──
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(home.Tile, neighbors);
            Dictionary<PlanetTile, (BiomeDef biome, float elevation, float temperature)> tileBefore = neighbors.ToDictionary(
                n => n, n => (n.Tile.PrimaryBiome, n.Tile.elevation, n.Tile.temperature));
            if (!CoastalLaunch.FindLaunchTile(home.Tile).Valid)
            {
                string noSea = CoastalLaunch.BlockReason(helm, out _, out _);
                ctx.Check(noSea == "DepthRim.Launch.NoSea".Translate().ToString(), $"with no open sea next door the launch is blocked ({noSea})");
            }
            PlanetTile seaTile = neighbors.FirstOrDefault(n => !Find.WorldObjects.AnyWorldObjectAt(n));
            ctx.Require(seaTile.Valid, "a free neighbouring tile");
            foreach (PlanetTile n in neighbors.Where(n => n != seaTile && IceConditions.At(n).OpenSea))
            {
                // 只留一格海，確認下水選的就是它
                n.Tile.PrimaryBiome = BiomeDefOf.TemperateForest;
                n.Tile.elevation = 100f;
            }
            seaTile.Tile.PrimaryBiome = BiomeDefOf.Ocean;
            seaTile.Tile.elevation = -500f;
            seaTile.Tile.temperature = 15f;
            Find.World.tileTemperatures.ClearCaches();
            ctx.Check(IceConditions.At(seaTile).OpenSea, "the neighbouring tile is now open sea");
            ctx.Check(CoastalLaunch.FindLaunchTile(home.Tile) == seaTile, "the launch goes to that tile");

            MapParent site = null;
            bool wasCurrent = Find.CurrentMap == home;
            try
            {
                string block = CoastalLaunch.BlockReason(helm, out _, out PlanetTile target);
                ctx.Require(block == null, $"the ship can launch ({block})");
                ctx.Check(target == seaTile, "target is the open sea next door");
                CoastalLaunch.quiet = true;
                ctx.Require(CoastalLaunch.Launch(helm), "launch started");
                yield return new WaitUntil(() => helm.Map != home, 600);
                site = Find.WorldObjects.MapParentAt(seaTile);
                ctx.Require(site is WorldObject_SeaSurfaceSite && site.HasMap, $"a sea-surface site holds the ship ({site?.GetType().Name})");
                Map sea = site.Map;
                List<IntVec3> hull = sea.AllCells.Where(c => c.GetTerrain(sea) == platform).ToList();
                ctx.Check(hull.Count == deck.Area, $"the whole deck came along ({hull.Count}/{deck.Area})");
                ctx.Check(MapComponent_IceSheet.RingAround(sea, hull).All(c => SeaSurface.IsOpenSea(c, sea)), "the ship floats on open sea");
                ctx.Check(new[] { keel, helm, thruster, battery }.All(t => t.Spawned && t.Map == sea), "keel, helm, thruster and battery are aboard");
                ctx.Check(cargo.Spawned && cargo.Map == sea && cargo.stackCount == 23, "the cargo came along");
                ctx.Check(sailor.Spawned && sailor.Map == sea && hull.Contains(sailor.Position), "the colonist on deck came along");
                ctx.Check(deck.Cells.All(c => home.terrainGrid.TerrainAt(c) == deep), $"the bay is deep ocean water again, no space left ({string.Join(", ", deck.Cells.Where(c => home.terrainGrid.TerrainAt(c) != deep).Take(5).Select(c => home.terrainGrid.TerrainAt(c)?.defName))})");
                ctx.Check(Find.LetterStack.LettersListForReading.Any(l => l.Label == "DepthRim.Launch.LetterLabel".Translate()), "a letter reports the launch");
                ctx.Check(!CoastalLaunch.IsShipyardMap(sea), "the sea map is not a shipyard: no launch there");
                // 壓載水艙（M6）是下潛唯一的門檻：灣裡造的船沒有，就只能浮航
                string dive = SubmarineSurfacing.DiveBlockReason(helm, out _, out _);
                ctx.Check(dive == "DepthRim.Ballast.None".Translate().ToString(), $"a ship without ballast tanks cannot dive ({dive})");
                foreach (Wait w in ctx.ScreenshotOn(sea, CellRect.FromCellList(hull).CenterCell, 14f, "coastal_launched")) { yield return w; }
                yield return new WaitFrames(30);
            }
            finally
            {
                CoastalLaunch.quiet = false;
                if (wasCurrent) { Current.Game.CurrentMap = home; }
                if (!sailor.Destroyed) { if (sailor.Spawned) { sailor.DeSpawn(); } sailor.Destroy(); }
                if (site != null && site.HasMap)
                {
                    Current.Game.DeinitAndRemoveMap(site.Map, false);
                }
                if (site != null && !site.Destroyed) { site.Destroy(); }
                foreach (Thing t in built.Concat(new[] { cargo }).Where(t => !t.Destroyed)) { t.Destroy(); }
                foreach (Thing t in bay.Cells.SelectMany(c => c.GetThingList(home)).Where(t => t.def == ThingDefOf.PowerConduit || t.def == ThingDefOf.Steel).ToList()) { t.Destroy(); }
                foreach (KeyValuePair<PlanetTile, (BiomeDef biome, float elevation, float temperature)> kv in tileBefore)
                {
                    kv.Key.Tile.PrimaryBiome = kv.Value.biome;
                    kv.Key.Tile.elevation = kv.Value.elevation;
                    kv.Key.Tile.temperature = kv.Value.temperature;
                }
                Find.World.tileTemperatures.ClearCaches();
                OceanRouting.ClearCache();
                foreach (KeyValuePair<IntVec3, TerrainDef> kv in original)
                {
                    if (home.terrainGrid.TerrainAt(kv.Key) != kv.Value) { home.terrainGrid.SetTerrain(kv.Key, kv.Value); }
                }
            }
            yield return new WaitFrames(2);
            ctx.Check(bay.Cells.All(c => home.terrainGrid.TerrainAt(c) == original[c]), "home terrain restored after the test");
        }

        private static bool map_NoBlockers(Map map, IntVec3 c)
        {
            return c.GetThingList(map).All(t => t is Plant || t.def.category == ThingCategory.Filth);
        }
    }
}
