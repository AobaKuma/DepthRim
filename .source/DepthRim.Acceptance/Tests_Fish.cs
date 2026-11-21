using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>漁獲（2026-11-16）：三種體型的通用魚依生態區與水溫套品種，死後變成品種的魚獲。</summary>
    public static class Tests_Fish
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>
        /// Q1：生成一張海藻林地圖：野生魚出現（小、中、大），每條都有同體型、棲地相符的品種，名稱與身體顏色跟著品種；
        /// 經過幾次水位結算後魚沒有溺水、涉水或水壓 hediff；殺死一條魚 → 沒有屍體，原地出現品種的魚獲（數量在範圍內）。
        /// </summary>
        public static IEnumerable<Wait> Q1_FishCatch(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            BiomeDef biome = DefDatabase<BiomeDef>.GetNamed("DepthRim_KelpForest");
            ctx.Require(DefDatabase<FishVariantDef>.AllDefsListForReading.Count >= 9, $"fish variants loaded ({DefDatabase<FishVariantDef>.AllDefsListForReading.Count})");
            PlanetTile tile = Tests_Ocean.FreeTileOf(ocean, biome, out BiomeDef original);
            ctx.Require(tile.Valid, "found a free kelp forest tile");

            MapParent site = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
            site.Tile = tile;
            site.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(site);
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(100, 1, 100), DepthRimDefOf.DepthRim_OceanSite);
            ctx.Require(map != null, "kelp forest map generated");
            Thing anchor = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.PowerConduit), map.Center, map, WipeMode.Vanish);
            anchor.SetFaction(Faction.OfPlayer);
            yield return new WaitFrames(2);

            List<Pawn> fish = map.mapPawns.AllPawnsSpawned.Where(CompFishCatch.IsAquatic).ToList();
            ctx.Note($"water {map.mapTemperature.OutdoorTemp:0.0} °C; fish: " + string.Join(", ", fish.GroupBy(p => p.def.defName + "/" + p.TryGetComp<CompFishCatch>().Variant?.defName).Select(g => g.Key + "×" + g.Count())));
            ctx.Check(fish.Count >= 5, $"wild fish spawned at map generation ({fish.Count})");
            // 體型組合是隨機的（地圖生成時只會出現幾群），只記錄不檢查
            ctx.Note($"fish sizes spawned: {string.Join(", ", fish.Select(p => p.def.defName).Distinct())}");
            DepthTier tier = FloodingUtility.DepthTierOf(map);
            foreach (Pawn p in fish)
            {
                CompFishCatch comp = p.TryGetComp<CompFishCatch>();
                FishVariantDef v = comp.Variant;
                if (!ctx.Check(v != null, $"{p.ThingID} has a variant")) { continue; }
                ctx.Check(v.size == comp.Props.size, $"{p.ThingID}: variant {v.defName} is {comp.Props.size}");
                ctx.Check(v.HabitatMatches(map.Biome, tier), $"{p.ThingID}: variant {v.defName} lives in {map.Biome.defName} ({tier})");
                ctx.Check(p.KindLabel == v.label, $"{p.ThingID}: shown as \"{p.KindLabel}\" (variant label \"{v.label}\")");
                ctx.Check(p.Faction == null && !p.RaceProps.EatsFood && p.needs.food == null && p.needs.rest == null, $"{p.ThingID}: wild, no food or rest need");
            }

            // 身體顏色：畫一次後 body 節點的 graphic 是品種色
            Pawn sample = fish.FirstOrDefault();
            if (sample != null)
            {
                foreach (Wait w in ctx.ScreenshotOn(map, sample.Position, 6f, "fish_" + sample.TryGetComp<CompFishCatch>().Variant?.defName, 30)) { yield return w; }
                Graphic body = sample.Drawer.renderer.BodyGraphic;
                Color want = sample.TryGetComp<CompFishCatch>().Variant.color;
                ctx.Check(body != null && Mathf.Abs(body.Color.r - want.r) < 0.01f && Mathf.Abs(body.Color.g - want.g) < 0.01f && Mathf.Abs(body.Color.b - want.b) < 0.01f,
                    $"body graphic is tinted with the variant colour ({body?.Color} vs {want})");
                ctx.Check(body?.path?.StartsWith("Thing/Fish/fish_") == true, $"body uses the generic fish texture ({body?.path})");
            }

            // 水位結算：魚不溺水、不涉水、不受水壓
            yield return Updates(3);
            fish = fish.Where(p => p.Spawned && !p.Dead).ToList();
            ctx.Check(fish.Count > 0, "fish still alive after flooding updates");
            ctx.Check(fish.All(p => !p.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Drowning) && !p.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Wading)
                && !p.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Barotrauma) && !p.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_PressureAdaptation)),
                "fish get no drowning, wading, barotrauma or pressure adaptation");

            // 殺死一條魚：變成魚獲
            Pawn victim = fish.OrderByDescending(p => p.BodySize).First();
            FishVariantDef variant = victim.TryGetComp<CompFishCatch>().Variant;
            IntVec3 cell = victim.Position;
            int before = map.listerThings.ThingsOfDef(variant.catchDef).Sum(t => t.stackCount);
            victim.Kill(null);
            yield return new WaitFrames(2);
            int after = map.listerThings.ThingsOfDef(variant.catchDef).Sum(t => t.stackCount);
            int gained = after - before;
            ctx.Check(victim.Dead, "the fish died");
            ctx.Check(victim.Corpse == null || victim.Corpse.Destroyed, "no fish corpse left behind");
            ctx.Check(!map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Any(c => ((Corpse)c).InnerPawn == victim), "no corpse of that fish on the map");
            ctx.Check(gained >= variant.catchCount.min && gained <= variant.catchCount.max, $"{variant.label} became {gained} {variant.catchDef.label} (range {variant.catchCount})");
            ctx.Check(map.listerThings.ThingsOfDef(variant.catchDef).Any(t => t.Position.InHorDistOf(cell, 3f)), "the catch lies where the fish died");

            Current.Game.CurrentMap = DepthRimFixture.Home;
            Current.Game.DeinitAndRemoveMap(map, false);
            if (!site.Destroyed) { site.Destroy(); }
            if (original != null) { tile.Tile.PrimaryBiome = original; }
            yield return new WaitFrames(30);
        }

        private static Pawn SpawnFish(string kind, IntVec3 cell, Map map)
        {
            Pawn fish = PawnGenerator.GeneratePawn(PawnKindDef.Named(kind));
            GenSpawn.Spawn(fish, cell, map, WipeMode.Vanish);
            return fish;
        }

        private static bool Suffocating(Pawn p) => p.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_FishOutOfWater);

        /// <summary>
        /// Q2：魚只活在海裡。種族旗標擋掉原版不看生態區的生成；非水下地圖（地表）上出現的魚很快被移除；
        /// 排乾的密閉艙室裡的魚一次水位結算就倒地、幾次後死亡並變成漁獲；倒地的魚回到水裡會恢復；開放水域的魚沒事。
        /// </summary>
        public static IEnumerable<Wait> Q2_FishNeedWater(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            foreach (PawnKindDef kind in DefDatabase<PawnKindDef>.AllDefs.Where(k => k.race.GetCompProperties<CompProperties_FishCatch>() != null))
            {
                ctx.Check(!kind.canArriveManhunter && !kind.RaceProps.herdMigrationAllowed && !kind.RaceProps.canBecomeShambler && kind.RaceProps.neverIncludeInQuests
                    && kind.race.tradeTags.NullOrEmpty() && kind.race.GetStatValueAbstract(StatDefOf.Wildness) >= 0.35f,
                    $"{kind.defName}: excluded from manhunter packs, herd migration, shamblers, quests, traders and farm animal wander-ins");
                List<BiomeDef> biomes = DefDatabase<BiomeDef>.AllDefs.Where(b => b.AllWildAnimals.Contains(kind)).ToList();
                // 箱網的睡鯊只由掠食者事件生成（ecoSystemWeight 0），不在任何生態區
                bool eventOnly = kind.ecoSystemWeight <= 0f;
                ctx.Check(eventOnly ? biomes.Count == 0 : biomes.Count > 0 && biomes.All(b => b.HasModExtension<WaterMapExtension>()), $"{kind.defName}: only underwater ocean biomes list it as wildlife ({string.Join(", ", biomes.Select(b => b.defName))})");
            }

            // 地表（沒有水）：魚不會消失，而是離水倒地、很快死成漁獲
            Map home = DepthRimFixture.Home;
            IntVec3 landCell = CellFinder.RandomClosewalkCellNear(home.Center, home, 10);
            Pawn stray = SpawnFish("DepthRim_Fish_Medium", landCell, home);
            FishVariantDef strayVariant = stray.TryGetComp<CompFishCatch>().Variant;
            int strayBefore = home.listerThings.ThingsOfDef(strayVariant.catchDef).Sum(t => t.stackCount);
            yield return Updates(1);
            ctx.Check(!stray.Destroyed && (stray.Dead || (Suffocating(stray) && stray.Downed)), "a fish on the dry surface map is out of water and downed (not removed)");
            WaitUntil strayDied = new WaitUntil(() => stray.Dead, MapComponent_Flooding.UpdateInterval * (FishHabitat.UpdatesToDeath + 2));
            yield return strayDied;
            yield return new WaitFrames(2);
            ctx.Check(!strayDied.TimedOut, "the fish on dry land dies quickly");
            ctx.Check(home.listerThings.ThingsOfDef(strayVariant.catchDef).Sum(t => t.stackCount) > strayBefore, "and becomes its catch");
            foreach (Thing t in home.listerThings.ThingsOfDef(strayVariant.catchDef).Where(t => t.Position.InHorDistOf(landCell, 4f)).ToList()) { t.Destroy(); }

            // 沒排乾就把艙室的水帶上地面層（船體搬移的 CopyLevels，同上浮／破冰）：魚還活著；艙室開了天窗（露天）水流光、魚窒息
            Tests_Flooding.BuildCompartment(ctx, out CellRect inner, out _);
            yield return Updates(1);
            ctx.Require(Tests_Flooding.Level(inner.CenterCell) >= 0.99f, $"the ocean compartment is flooded ({Tests_Flooding.Level(inner.CenterCell):P0})");
            CellRect homeRing = FreeRectOn(home, inner.Width + 2);
            ctx.Require(homeRing.Width > 0, "found room for a compartment on the surface map");
            CellRect homeInner = homeRing.ContractedBy(1);
            foreach (IntVec3 c in homeRing.EdgeCells) { DepthRimFixture.Spawn(ThingDefOf.Wall, c, home, ThingDefOf.Steel); }
            foreach (IntVec3 c in homeInner) { home.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed); }
            yield return new WaitFrames(2);
            MapComponent_Flooding homeFlooding = home.Flooding();
            ctx.Check(!homeFlooding.HasWater, "the surface map has no water system before anything is carried up");
            homeFlooding.CopyLevels(DepthRimFixture.Flooding, inner.Cells, homeInner.Min - inner.Min);
            FloodingUtility.WaterChanged(home);
            ctx.Check(homeFlooding.HoldsWater && !homeFlooding.Active, "carrying a flooded compartment up makes the surface map hold water (not a sea map)");
            Pawn carried = SpawnFish("DepthRim_Fish_Small", homeInner.CenterCell, home);
            yield return Updates(2);
            float carriedLevel = homeFlooding.LevelAt(homeInner.CenterCell);
            ctx.Check(carriedLevel >= 0.95f, $"the carried water stays in the sealed compartment on the surface ({carriedLevel:P0})");
            ctx.Check(homeFlooding.LevelAt(homeRing.ExpandedBy(1).EdgeCells.First()) == 0f, "outside the compartment the surface map is dry (air, not sea)");
            ctx.Check(!carried.Dead && !Suffocating(carried) && !carried.Downed, "a fish in carried water on the surface stays alive");
            FishVariantDef carriedVariant = carried.TryGetComp<CompFishCatch>().Variant;
            int carriedBefore = home.listerThings.ThingsOfDef(carriedVariant.catchDef).Sum(t => t.stackCount);
            // 開天窗：房間露天 -> 地面層的開放房間是空氣，積水流光
            home.roofGrid.SetRoof(homeInner.Min, null);
            yield return Updates(1);
            ctx.Check(homeFlooding.LevelAt(homeInner.CenterCell) == 0f, $"opening the roof lets the water out ({homeFlooding.LevelAt(homeInner.CenterCell):P0})");
            ctx.Check(carried.Dead || (Suffocating(carried) && carried.Downed), "the fish is now out of water");
            ctx.Check(!homeFlooding.HoldsWater, "with no water left the surface map stops holding water");
            WaitUntil carriedDied = new WaitUntil(() => carried.Dead, MapComponent_Flooding.UpdateInterval * (FishHabitat.UpdatesToDeath + 2));
            yield return carriedDied;
            yield return new WaitFrames(2);
            ctx.Check(!carriedDied.TimedOut && home.listerThings.ThingsOfDef(carriedVariant.catchDef).Sum(t => t.stackCount) > carriedBefore, "it dies and becomes its catch");
            foreach (IntVec3 c in homeInner) { home.roofGrid.SetRoof(c, null); }
            foreach (Thing t in home.listerThings.ThingsOfDef(carriedVariant.catchDef).Where(t => homeRing.Contains(t.Position)).ToList()) { t.Destroy(); }

            // 排乾的密閉艙室（同一間，抽乾）
            foreach (Wait w in Tests_Flooding.DrainCompartment(ctx, inner)) { yield return w; }
            ctx.Require(Tests_Flooding.Level(inner.CenterCell) < FishHabitat.MinWaterLevel, $"compartment is dry ({Tests_Flooding.Level(inner.CenterCell):P0})");
            IntVec3 dryCell = inner.Cells.First(c => c.Standable(map) && c.GetFirstPawn(map) == null && c != inner.CenterCell && c != inner.CenterCell + IntVec3.North);
            IntVec3 rescueCell = inner.Cells.Last(c => c.Standable(map) && c.GetFirstPawn(map) == null && c != dryCell && c != inner.CenterCell && c != inner.CenterCell + IntVec3.North);
            IntVec3 waterCell = DepthRimFixture.OpenWaterCell();
            Pawn dry = SpawnFish("DepthRim_Fish_Large", dryCell, map);
            Pawn rescued = SpawnFish("DepthRim_Fish_Small", rescueCell, map);
            Pawn wet = SpawnFish("DepthRim_Fish_Small", waterCell, map);
            FishVariantDef variant = dry.TryGetComp<CompFishCatch>().Variant;
            int before = map.listerThings.ThingsOfDef(variant.catchDef).Sum(t => t.stackCount);

            yield return Updates(1);
            ctx.Check(Suffocating(dry) && dry.Downed, $"a fish in the dry compartment is out of water and downed at once (downed {dry.Downed})");
            ctx.Check(Suffocating(rescued) && rescued.Downed, "the second dry fish is downed too");
            ctx.Check(!Suffocating(wet) && !wet.Downed && !wet.Dead, "a fish in open water is fine");

            // 倒地的魚放回水裡：恢復
            rescued.DeSpawn();
            GenSpawn.Spawn(rescued, waterCell, map, WipeMode.Vanish);
            yield return Updates(1);
            ctx.Check(!Suffocating(rescued) && !rescued.Downed && !rescued.Dead, "a downed fish put back into the water recovers");

            int start = Find.TickManager.TicksGame;
            WaitUntil died = new WaitUntil(() => dry.Dead, MapComponent_Flooding.UpdateInterval * (FishHabitat.UpdatesToDeath + 2));
            yield return died;
            ctx.Check(!died.TimedOut, $"the stranded fish dies quickly ({Find.TickManager.TicksGame - start} ticks after being downed; at most {FishHabitat.UpdatesToDeath} updates in all)");
            yield return new WaitFrames(2);
            int gained = map.listerThings.ThingsOfDef(variant.catchDef).Sum(t => t.stackCount) - before;
            ctx.Check(gained >= variant.catchCount.min && gained <= variant.catchCount.max, $"the dead fish became {gained} {variant.catchDef.label} ({variant.catchCount})");
            ctx.Check(map.listerThings.ThingsOfDef(variant.catchDef).Any(t => inner.Contains(t.Position)), "the catch lies in the compartment");
            ctx.Check(!wet.Dead && !Suffocating(wet), "the fish in open water is still fine");

            foreach (Pawn p in new[] { rescued, wet })
            {
                if (!p.Destroyed) { p.Destroy(DestroyMode.Vanish); }
            }
            foreach (Thing t in map.listerThings.ThingsOfDef(variant.catchDef).Where(t => inner.Contains(t.Position)).ToList()) { t.Destroy(); }
        }

        /// <summary>地圖上一塊空的正方形（可站、沒有建築、物品、Pawn、屋頂，外圍多一格也是），從中心往外找。</summary>
        private static CellRect FreeRectOn(Map map, int size)
        {
            foreach (IntVec3 c in GenRadial.RadialCellsAround(map.Center, 40f, true))
            {
                CellRect rect = new CellRect(c.x, c.z, size, size);
                CellRect outer = rect.ExpandedBy(1);
                if (!outer.InBounds(map)) { continue; }
                if (outer.Cells.All(x => x.Standable(map) && x.GetEdifice(map) == null && !x.Roofed(map) && x.GetFirstPawn(map) == null && x.GetFirstItem(map) == null
                    && x.GetFirstThing<Plant>(map) == null && !x.GetTerrain(map).IsWater))
                {
                    return rect;
                }
            }
            return new CellRect(0, 0, 0, 0);
        }

        /// <summary>
        /// Q3：游離地圖（比照 Odyssey 的鳥）：開放水域（沒有屋頂）的魚受驚時直接往上游走、離開地圖；
        /// 有屋頂的艙室裡的魚只會逃跑，不會離場；野生魚可以從開放水域上方游進地圖。
        /// </summary>
        public static IEnumerable<Wait> Q3_FishSwimOff(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            IntVec3 waterCell = DepthRimFixture.OpenWaterCell();
            ctx.Require(!waterCell.Roofed(map), "open water is unroofed");
            Pawn fish = SpawnFish("DepthRim_Fish_Medium", waterCell, map);
            ctx.Check(fish.flight != null && fish.flight.CanEverFly && fish.RaceProps.canLeaveMapFlying, "fish can leave the map like birds");
            Pawn diver = DepthRimFixture.SpawnColonist(map, CellFinder.RandomClosewalkCellNear(waterCell, map, 4, c => c != waterCell));
            fish.mindState.StartFleeingBecauseOfPawnAction(diver);
            ctx.Check(fish.CurJobDef == JobDefOf.ExitMapFlying, $"a startled fish in open water swims off the map ({fish.CurJobDef?.defName})");
            WaitUntil gone = new WaitUntil(() => !fish.Spawned, 120);
            yield return gone;
            ctx.Check(!gone.TimedOut && !fish.Dead, "it leaves its cell upwards, alive");
            WaitUntil left = new WaitUntil(() => !(fish.ParentHolder is Skyfaller), 1200);
            yield return left;
            ctx.Check(!left.TimedOut && !map.mapPawns.AllPawns.Contains(fish), "and is gone from the map");

            // 有屋頂（密閉、灌滿的艙室）：只逃跑，不離場
            Tests_Flooding.BuildCompartment(ctx, out CellRect inner, out _);
            yield return Updates(1);
            IntVec3 roomCell = inner.Cells.First(c => c.Standable(map) && c.GetFirstPawn(map) == null);
            Pawn trapped = SpawnFish("DepthRim_Fish_Medium", roomCell, map);
            Pawn diver2 = DepthRimFixture.SpawnColonist(map, inner.Cells.Last(c => c.Standable(map) && c.GetFirstPawn(map) == null));
            trapped.mindState.StartFleeingBecauseOfPawnAction(diver2);
            ctx.Check(trapped.CurJobDef != JobDefOf.ExitMapFlying, $"a fish under a roof does not swim off ({trapped.CurJobDef?.defName})");
            yield return new WaitTicks(60);
            ctx.Check(trapped.Spawned, "it is still in the compartment");

            // 游進地圖：從開放水域上方
            PawnKindDef small = PawnKindDef.Named("DepthRim_Fish_Small");
            int before = map.mapPawns.AllPawnsSpawned.Count(p => p.kindDef == small);
            bool spawned = map.wildAnimalSpawner.SpawnRandomWildAnimalAt(waterCell, true, small);
            ctx.Check(spawned, "the wild spawner can fly fish in");
            ctx.Check(map.listerThings.ThingsOfDef(ThingDefOf.FlyerArrival).Any(), "they arrive from above (FlyerArrival)");
            WaitUntil arrived = new WaitUntil(() => map.mapPawns.AllPawnsSpawned.Count(p => p.kindDef == small) > before, 1200);
            yield return arrived;
            List<Pawn> newcomers = map.mapPawns.AllPawnsSpawned.Where(p => p.kindDef == small).ToList();
            ctx.Check(!arrived.TimedOut, $"fish swam in ({newcomers.Count - before})");
            ctx.Check(newcomers.All(p => p.TryGetComp<CompFishCatch>().Variant != null && !p.Position.Roofed(map)), "newcomers have a variant and land in open water");

            foreach (Pawn p in newcomers.Concat(new[] { trapped }).Where(p => !p.Destroyed).ToList()) { p.Destroy(DestroyMode.Vanish); }
        }
    }
}
