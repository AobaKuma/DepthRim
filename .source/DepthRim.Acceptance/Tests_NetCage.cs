using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DepthRim.Acceptance
{
    /// <summary>箱網養殖（Docs/箱網養殖_內容設計.md §15.2，NC0～NC12）。</summary>
    public static class Tests_NetCage
    {
        // ══════════════════════════════════════════════════════════════
        //  共用
        // ══════════════════════════════════════════════════════════════

        /// <summary>原版海岸地圖上挖一個灣：7×7 箱網的位置與外圍一圈是海水，南邊那一排保留陸地當岸。</summary>
        private sealed class Bay
        {
            public Map map;
            public CellRect cage;
            public readonly Dictionary<IntVec3, TerrainDef> original = new Dictionary<IntVec3, TerrainDef>();

            public IntVec3 Shore => new IntVec3(cage.CenterCell.x, 0, cage.minZ - 1);

            public void Restore()
            {
                foreach (KeyValuePair<IntVec3, TerrainDef> kv in original)
                {
                    foreach (Thing t in kv.Key.GetThingList(map).ToList())
                    {
                        if (!(t is Pawn) && !t.Destroyed && t.def.destroyable) { t.Destroy(DestroyMode.Vanish); }
                    }
                    map.terrainGrid.SetTerrain(kv.Key, kv.Value);
                }
            }
        }

        private static Bay MakeBay(TestContext ctx, Map home, TerrainDef inner, TerrainDef ring)
        {
            bool Free(IntVec3 c) => c.InBounds(home) && !c.Fogged(home) && c.GetEdifice(home) == null && c.GetFirstPawn(home) == null
                && home.terrainGrid.UnderTerrainAt(c) == null && home.terrainGrid.TerrainAt(c) is TerrainDef t && !t.IsWater && t.natural
                && !c.GetThingList(home).Any(x => x.def.category == ThingCategory.Building || x.def.category == ThingCategory.Item);
            CellRect patch = CellRect.Empty;
            foreach (IntVec3 c in home.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 11, 11);
                if (r.minX >= 12 && r.minZ >= 12 && r.maxX < home.Size.x - 12 && r.maxZ < home.Size.z - 12 && r.Cells.All(Free))
                {
                    patch = r;
                    break;
                }
            }
            ctx.Require(patch.Area > 0, "found a clear 11x11 patch for a bay");
            Bay bay = new Bay { map = home, cage = new CellRect(patch.minX + 2, patch.minZ + 2, 7, 7) };
            CellRect ringRect = bay.cage.ExpandedBy(1);
            foreach (IntVec3 c in ringRect)
            {
                bay.original[c] = home.terrainGrid.TerrainAt(c);
                foreach (Thing t in c.GetThingList(home).Where(t => t is Plant || t.def.category == ThingCategory.Filth).ToList()) { t.Destroy(); }
                if (c.z == ringRect.minZ && !bay.cage.Contains(c))
                {
                    continue; // 南邊一排：岸
                }
                home.terrainGrid.SetTerrain(c, bay.cage.Contains(c) ? inner : ring);
            }
            return bay;
        }

        private static Building_NetCage SpawnCage(ThingDef def, Map map, CellRect rect)
        {
            return (Building_NetCage)DepthRimFixture.Spawn(def, rect.CenterCell, map);
        }

        /// <summary>海面地圖：中央 7×7 是開放海面（箱網），南邊一排船體平台當甲板。</summary>
        private static CellRect SeaCageSpot(Map sea, IntVec3 center, bool hull = true)
        {
            CellRect rect = CellRect.CenteredOn(center, 3);
            if (hull)
            {
                Tests_SeaSurface.LayHull(sea, new CellRect(rect.minX, rect.minZ - 3, 7, 3));
            }
            return rect;
        }

        private static AquacultureSpeciesDef FittingSpecies(CompNetCage comp, Func<AquacultureSpeciesDef, bool> extra = null)
        {
            return DefDatabase<AquacultureSpeciesDef>.AllDefsListForReading
                .Where(s => comp.CannotStockReason(s) == null && (extra == null || extra(s)))
                .OrderBy(s => s.TemperatureDistance(comp.CurrentWaterTemp())).FirstOrDefault();
        }

        private static void AdvanceDays(CompNetCage comp, float days)
        {
            int steps = Mathf.RoundToInt(days * GenDate.TicksPerDay / CompNetCage.TickInterval);
            for (int i = 0; i < steps; i++)
            {
                comp.AddFeed(comp.Props.feedCapacity);
                comp.Advance(CompNetCage.TickInterval);
            }
        }

        private static IEnumerable<Wait> RunJob(TestContext ctx, Pawn pawn, Job job, string what, int timeout = 6000)
        {
            ctx.Require(job != null, what + ": a job is offered");
            JobDef def = job.def;
            // 地圖上其他殖民者也會被真的 WorkGiver 派去做箱網的工作：先放掉他們的預約
            if (job.targetA.HasThing && pawn.Map != null)
            {
                pawn.Map.reservationManager.ReleaseAllForTarget(job.targetA.Thing);
            }
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);
            WaitUntil w = new WaitUntil(() => pawn.CurJobDef != def, timeout);
            yield return w;
            ctx.Check(!w.TimedOut, what + ": the job finishes");
        }

        private static readonly List<Action> restores = new List<Action>();

        private static void ResetDebug()
        {
            CompNetCage.debugOxygen = null;
            CompNetCage.debugAlwaysBreach = false;
            DiseaseSpread.debugAlwaysInfect = false;
            MapComponent_SeaWeather.debugAlwaysHit = false;
            MapComponent_Aquaculture.debugNoEvents = false;
            foreach (Action a in restores) { a(); }
            restores.Clear();
        }

        // ══════════════════════════════════════════════════════════════
        //  NC0／NC2：灣內箱網，從放養到收成
        // ══════════════════════════════════════════════════════════════

        public static IEnumerable<Wait> NC0_BayCycle(TestContext ctx)
        {
            Map home = DepthRimFixture.Home;
            Current.Game.CurrentMap = home;
            MapComponent_Aquaculture.debugNoEvents = true;
            Bay bay = MakeBay(ctx, home, TerrainDefOf.WaterOceanShallow, TerrainDefOf.WaterOceanDeep);
            try
            {
                ThingDef shallow = DepthRimDefOf.DepthRim_NetCage_Shallow;
                MapComponent_Aquaculture aqua = MapComponent_Aquaculture.For(home);
                ctx.Require(aqua != null, "the home map has the aquaculture component");
                ctx.Check(aqua.Venue == CageVenue.Bay, "a vanilla coastal map is a bay");
                AcceptanceReport ok = PlaceWorker_NetCage.Check(shallow.GetCompProperties<CompProperties_NetCage>(), bay.cage, home);
                ctx.Check(ok.Accepted, $"a surface net fits in the bay next to the shore ({ok.Reason})");
                ctx.Check(GenConstruct.CanPlaceBlueprintAt(shallow, bay.cage.CenterCell, Rot4.North, home).Accepted, "and the blueprint can be placed (hull-base affordance on ocean water)");
                ctx.Check(!PlaceWorker_NetCage.Check(shallow.GetCompProperties<CompProperties_NetCage>(), bay.cage.MovedBy(new IntVec2(0, -1)), home).Accepted, "a cage overlapping the shore is refused");
                ctx.Check(!PlaceWorker_NetCage.Check(DepthRimDefOf.DepthRim_NetCage_Deep.GetCompProperties<CompProperties_NetCage>(), bay.cage, home).Accepted, "deep nets are refused in the bay");

                Building_NetCage cage = SpawnCage(shallow, home, bay.cage);
                CompNetCage comp = cage.Cage;
                ctx.Check(cage.OccupiedRect() == bay.cage, $"the cage covers the 7x7 bay ({cage.OccupiedRect()})");
                ctx.Check(aqua.Cages.Contains(cage), "the cage registers with the map component");
                ctx.Check(cage.DrawColor.g > cage.DrawColor.b, "bay cages are tinted green");
                ctx.Check(Mathf.Abs(comp.WaterDepth - 10f) < 0.01f, $"bay shallow water is 10 m ({comp.WaterDepth})");
                float expected = NetCageMath.BayWaterTemperature(aqua.AirAnnual, home.mapTemperature.OutdoorTemp);
                ctx.Check(Mathf.Abs(comp.CurrentWaterTemp() - expected) < 0.01f, $"water temperature uses the bay model ({comp.CurrentWaterTemp():F1} °C)");

                // ── 放養（工作）──
                AquacultureSpeciesDef sp = FittingSpecies(comp, s => s.coastal);
                ctx.Require(sp != null, $"some coastal species suits {comp.CurrentWaterTemp():F1} °C");
                ctx.Check(DefDatabase<AquacultureSpeciesDef>.AllDefs.Where(s => !s.coastal).All(s => comp.CannotStockReason(s) != null), "open-sea species cannot be stocked in the bay");
                comp.OrderStock(sp, sp.stockCount);
                Pawn worker = DepthRimFixture.SpawnColonist(home, bay.Shore);
                worker.skills.GetSkill(SkillDefOf.Animals).Level = 10;
                ctx.Check(new WorkGiver_StockCage().HasJobOnThing(worker, cage), "stocking work is offered");
                foreach (Wait w in RunJob(ctx, worker, new WorkGiver_StockCage().JobOnThing(worker, cage), "stocking")) { yield return w; }
                ctx.Check(comp.HasFish && comp.Species == sp && comp.Count == sp.stockCount, $"{sp.label} x{comp.Count} stocked");

                // ── 補飼料（工作）──
                Thing kibble = DepthRimFixture.Spawn(ThingDefOf.Kibble, bay.Shore + IntVec3.West, home);
                kibble.stackCount = 75;
                ctx.Check(comp.NeedsFeed, "an empty feed store needs kibble");
                foreach (Wait w in RunJob(ctx, worker, new WorkGiver_RefillCageFeed().JobOnThing(worker, cage), "feeding")) { yield return w; }
                ctx.Check(comp.FeedStore >= 60f, $"the kibble went into the feed store ({comp.FeedStore:F0}, some already eaten)");

                // ── 雷雨：溶氧 −0.08、成長 ×0.8，沒有海況傷害 ──
                home.weatherManager.TransitionTo(WeatherDef.Named("RainyThunderstorm"));
                home.weatherManager.curWeather = WeatherDef.Named("RainyThunderstorm");
                home.weatherManager.curWeatherAge = 99999;
                comp.Advance(CompNetCage.TickInterval);
                ctx.Check(aqua.BayRoughWeather, "a thunderstorm counts as rough bay weather");
                ctx.Check(comp.Environment.oxygenExtra <= -NetCageMath.BayWeatherPenalty + 0.0001f && Mathf.Abs(comp.Environment.growthFactor - NetCageMath.BayStormGrowth * aqua.VanillaClimate.growth) < 0.01f,
                    $"rain: oxygen -0.08, growth x0.8 ({comp.Environment.oxygenExtra:F2}, {comp.Environment.growthFactor:F2})");
                int hp = cage.HitPoints;
                yield return new WaitTicks(600);
                ctx.Check(cage.HitPoints == hp, "no sea-state damage in the bay");
                home.weatherManager.curWeather = WeatherDefOf.Clear;

                // ── 成長到上市：飼料與體重照公式 ──
                float w0 = comp.AvgWeight;
                AdvanceDays(comp, 1f);
                float oneDay = comp.AvgWeight - w0;
                float formula = NetCageMath.GrowthPerDay(sp.harvestWeight, sp.growDays, sp.TemperatureDistance(comp.WaterTemp), comp.Oxygen, false, comp.Density, true, comp.Environment.growthFactor);
                ctx.Check(Mathf.Abs(oneDay - formula) < formula * 0.2f + 0.001f, $"one day of growth follows the formula ({oneDay:F3} vs {formula:F3} kg)");
                float feed0 = comp.TotalFeedEaten;
                for (int d = 0; d < 60 && !comp.Ready && comp.HasFish; d++) { AdvanceDays(comp, 1f); }
                ctx.Check(comp.Ready, $"the fish reach market weight ({comp.WeightRatio:P0}, {comp.TotalFeedEaten:F0} kibble eaten)");
                ctx.Check(comp.Count > sp.stockCount * 0.8f, $"most fish survive ({comp.Count}/{sp.stockCount})");
                ctx.Check(comp.TotalFeedEaten - feed0 > 50f, "the fish ate kibble while growing");
                ctx.Check(!new WorkGiver_HarvestCage().HasJobOnThing(worker, cage), "no harvest work until ordered");

                // ── 收成（工作）──
                int fish = comp.Count;
                float ratio = Mathf.Min(1f, comp.WeightRatio);
                ThingDef catchDef = sp.CatchDef;
                int before = home.listerThings.ThingsOfDef(catchDef).Sum(t => t.stackCount);
                HarmonyLib.Traverse.Create(comp).Field("harvestOrdered").SetValue(true);
                foreach (Wait w in RunJob(ctx, worker, new WorkGiver_HarvestCage().JobOnThing(worker, cage), "harvesting")) { yield return w; }
                int got = home.listerThings.ThingsOfDef(catchDef).Sum(t => t.stackCount) - before;
                int min = Mathf.RoundToInt(fish * sp.YieldPerFish.min * NetCageMath.OverripeFactor(comp.OverripeDays)) - 1;
                ctx.Check(got >= fish * sp.YieldPerFish.min * 0.5f && got <= fish * sp.YieldPerFish.max, $"harvest gives {got} {catchDef.label} for {fish} fish (each {sp.YieldPerFish})");
                ctx.Check(!comp.HasFish, "the cage is empty after the harvest");
                Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), false);
                foreach (Wait w in ctx.ScreenshotOn(home, bay.cage.CenterCell, 14f, "netcage_bay", 30)) { yield return w; }
                yield return new WaitFrames(40); // 截圖晚一幀才寫出：別在寫出前就拆掉地圖
            }
            finally
            {
                ResetDebug();
                bay.Restore();
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NC1：兩種場地的放置
        // ══════════════════════════════════════════════════════════════

        public static IEnumerable<Wait> NC1_Placement(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            try
            {
                yield return new WaitFrames(2);
                CompProperties_NetCage shallow = DepthRimDefOf.DepthRim_NetCage_Shallow.GetCompProperties<CompProperties_NetCage>();
                MapComponent_Aquaculture aqua = MapComponent_Aquaculture.For(sea);
                ctx.Check(aqua.Venue == CageVenue.OpenSea, "the sea-surface map is the open sea");
                IntVec3 c = sea.Center;
                CellRect noAccess = CellRect.CenteredOn(c + new IntVec3(30, 0, 0), 3);
                ctx.Check(PlaceWorker_NetCage.Check(shallow, noAccess, sea).Reason == "DepthRim.NetCage.NoAccess".Translate().ToString(), "open sea with nothing alongside: refused (no access)");
                CellRect spot = SeaCageSpot(sea, c);
                AcceptanceReport ok = PlaceWorker_NetCage.Check(shallow, spot, sea);
                ctx.Check(ok.Accepted, $"next to a hull deck the surface net fits ({ok.Reason})");
                ctx.Check(!PlaceWorker_NetCage.Check(shallow, spot.MovedBy(new IntVec2(0, -2)), sea).Accepted, "a cage over the hull deck is refused");
                ctx.Check(DepthRimDefOf.DepthRim_NetCage_Shallow.size == new IntVec2(7, 7) && !DepthRimDefOf.DepthRim_NetCage_Shallow.rotatable, "7x7, not rotatable");
                Building_NetCage cage = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, spot);
                ctx.Check(cage.DrawColor.b > cage.DrawColor.g, "open-sea cages are tinted blue");
                ctx.Check(cage.AccessCell().IsValid && cage.AccessCell().Standable(sea), "the access cell is on the deck");

                // 平台層
                Building_PillarFoot foot = Tests_SeaSurface.BuildPillar(sea, new IntVec3(c.x + 20, 0, c.z));
                yield return new WaitFrames(3);
                Map platform = SeaLevels.PlatformLevelOf(sea);
                if (platform != null)
                {
                    ctx.Check(!PlaceWorker_NetCage.Check(shallow, CellRect.CenteredOn(platform.Center, 3), platform).Accepted, "not on the platform level");
                }
                else
                {
                    ctx.Note("no platform level was created; skipped that check");
                }
                // 灣內海床（陸地）
                Map home = DepthRimFixture.Home;
                CellRect land = CellRect.CenteredOn(home.Center, 3);
                ctx.Check(!PlaceWorker_NetCage.Check(shallow, land, home).Accepted, "not on land");
            }
            finally
            {
                ResetDebug();
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NC3：水質（曝氣、藻華、缺氧死亡、警報）
        // ══════════════════════════════════════════════════════════════

        public static IEnumerable<Wait> NC3_WaterQuality(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            MapComponent_Aquaculture.debugNoEvents = true;
            try
            {
                yield return new WaitFrames(2);
                CellRect spot = SeaCageSpot(sea, sea.Center);
                Building_NetCage cage = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, spot);
                CompNetCage comp = cage.Cage;
                AquacultureSpeciesDef sp = FittingSpecies(comp) ?? DefDatabase<AquacultureSpeciesDef>.GetNamed("DepthRim_Aquaculture_Cod");
                comp.Stock(sp, sp.stockCount);
                comp.AddFeed(240f);
                comp.Advance(250);
                float o0 = comp.Oxygen;

                Building aerator = (Building)DepthRimFixture.Spawn(DepthRimDefOf.DepthRim_Aerator, new IntVec3(spot.CenterCell.x, 0, spot.minZ - 1), sea);
                CompPowerTrader power = aerator.GetComp<CompPowerTrader>();
                power.PowerOn = true;
                comp.Advance(250);
                ctx.Check(comp.Environment.aerators == 1 && Mathf.Abs(comp.Oxygen - Mathf.Min(1f, o0 + 0.15f)) < 0.02f, $"a powered aerator adds 0.15 ({o0:F2} -> {comp.Oxygen:F2})");
                power.PowerOn = false;
                comp.Advance(250);
                ctx.Check(comp.Environment.aerators == 0, "an unpowered aerator does nothing");

                sea.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(DepthRimDefOf.DepthRim_AlgalBloom, GenDate.TicksPerDay));
                comp.Advance(250);
                ctx.Check(Mathf.Abs(comp.Oxygen - Mathf.Max(0f, o0 - 0.18f)) < 0.02f, $"an algal bloom takes 0.18 ({comp.Oxygen:F2})");
                sea.gameConditionManager.ActiveConditions.Where(g => g.def == DepthRimDefOf.DepthRim_AlgalBloom).ToList().ForEach(g => g.End());

                // 警報
                CompNetCage.debugOxygen = 0.3f;
                comp.Advance(250);
                Alert_CageHypoxia alert = new Alert_CageHypoxia();
                ctx.Check(alert.GetReport().active, "low oxygen raises the hypoxia alert");
                CompNetCage.debugOxygen = 0.9f;
                comp.Advance(250);
                ctx.Check(!alert.GetReport().active, "and it clears when the oxygen recovers");

                // 缺氧死亡：0.1 持續 6 小時
                CompNetCage.debugOxygen = 0.1f;
                int before = comp.Count;
                int letters = Find.LetterStack.LettersListForReading.Count;
                for (int i = 0; i < NetCageMath.LowOxygenTicks / 250 + 1; i++) { comp.Advance(250); }
                float lost = 1f - comp.Count / (float)before;
                ctx.Check(lost >= 0.19f && lost <= 0.55f, $"suffocation kills 20~50% ({lost:P0})");
                ctx.Check(Find.LetterStack.LettersListForReading.Count > letters, "with a letter");
            }
            finally
            {
                ResetDebug();
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NC4／NC8：外海風暴、破網、逃逸、修網
        // ══════════════════════════════════════════════════════════════

        public static IEnumerable<Wait> NC4_Storm(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            MapComponent_Aquaculture.debugNoEvents = true;
            try
            {
                yield return new WaitFrames(2);
                CellRect spot = SeaCageSpot(sea, sea.Center);
                Building_NetCage cage = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, spot);
                CompNetCage comp = cage.Cage;
                AquacultureSpeciesDef sp = FittingSpecies(comp) ?? DefDatabase<AquacultureSpeciesDef>.GetNamed("DepthRim_Aquaculture_Cod");
                comp.Stock(sp, sp.stockCount);
                comp.AddFeed(240f);
                WeatherDef storm = WeatherDef.Named("DepthRim_RoughSeaStorm");
                sea.weatherManager.curWeather = storm;
                sea.weatherManager.lastWeather = storm;
                sea.weatherManager.curWeatherAge = 99999;
                comp.Advance(250);
                ctx.Check(Mathf.Abs(comp.Environment.growthFactor / sea.GetComponent<MapComponent_Aquaculture>().VanillaClimate.growth - 0.5f) < 0.01f, $"a storm halves surface-net growth ({comp.Environment.growthFactor:F2})");

                MapComponent_SeaWeather weather = sea.GetComponent<MapComponent_SeaWeather>();
                MapComponent_SeaWeather.debugAlwaysHit = true;
                int max = cage.MaxHitPoints;
                int floor = Mathf.CeilToInt(max * MapComponent_SeaWeather.BuildingHpFloor);
                int hp0 = cage.HitPoints;
                weather.BatterBuildings(SeaState.Of(sea));
                int dmg = hp0 - cage.HitPoints;
                FloatRange range = SeaState.Of(sea).buildingDamage;
                ctx.Check(dmg >= Mathf.RoundToInt(range.min * 1.5f) - 1 && dmg <= Mathf.RoundToInt(range.max * 1.5f) + 1, $"sea-state damage x1.5 ({dmg}, base {range})");
                for (int i = 0; i < 300 && cage.HitPoints > floor; i++) { weather.BatterBuildings(SeaState.Of(sea)); }
                ctx.Check(!cage.Destroyed && cage.HitPoints == floor, $"never below 20% ({cage.HitPoints}/{max})");

                // 破網
                cage.HitPoints = Mathf.RoundToInt(max * 0.45f);
                CompNetCage.debugAlwaysBreach = true;
                int count0 = comp.Count;
                int escapedBefore = comp.EscapePawnsSpawned;
                weather.BatterBuildings(SeaState.Of(sea));
                CompNetCage.debugAlwaysBreach = false;
                ctx.Check(comp.Count < count0 && comp.TotalEscaped > 0, $"the torn net lets fish escape ({count0} -> {comp.Count})");
                int escapedNow = comp.EscapePawnsSpawned - escapedBefore;
                ctx.Check(escapedNow >= 1 && escapedNow <= NetCageMath.MaxEscapePawns, $"at most 10 escaped fish appear ({escapedNow})");
                ctx.Check(new Alert_CageNetBreach().GetReport().active, "the damaged-net alert is up");
                yield return new WaitTicks(120);
                ctx.Check(!sea.mapPawns.AllPawnsSpawned.Any(p => CompFishCatch.IsAquatic(p) && p.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_FishOutOfWater)), "escaped fish on the open sea do not suffocate");

                // 修網（工作）
                sea.weatherManager.curWeather = WeatherDefOf.Clear;
                sea.weatherManager.lastWeather = WeatherDefOf.Clear;
                sea.weatherManager.curWeatherAge = 99999;
                MapComponent_SeaWeather.debugAlwaysHit = false;
                Pawn worker = DepthRimFixture.SpawnColonist(sea, cage.AccessCell());
                worker.jobs.debugLog = true;
                Thing cloth = DepthRimFixture.Spawn(ThingDefOf.Cloth, cage.AccessCell() + IntVec3.South, sea);
                cloth.stackCount = 60;
                Thing steel = DepthRimFixture.Spawn(ThingDefOf.Steel, cage.AccessCell() + IntVec3.South + IntVec3.East, sea);
                steel.stackCount = 40;
                int hpBefore = cage.HitPoints;
                WorkGiver_RepairCageNet giver = new WorkGiver_RepairCageNet();
                for (int i = 0; i < 4 && cage.HitPoints == hpBefore; i++)
                {
                    Job job = giver.JobOnThing(worker, cage);
                    if (job == null) { break; }
                    string jobName = job.def.defName;
                    foreach (Wait w in RunJob(ctx, worker, job, "mending step " + i)) { yield return w; }
                    ctx.Note($"mending step {i}: {jobName}, HP {cage.HitPoints}, needs repair {cage.Cage.NeedsRepair}, can work {cage.Cage.CanDoRepairWork}");
                }
                ctx.Check(cage.HitPoints == Mathf.Min(max, hpBefore + CompNetCage.RepairHp), $"mending restores 90 HP ({hpBefore} -> {cage.HitPoints})");
                ctx.Check(!sea.listerBuildingsRepairable.Contains(Faction.OfPlayer, cage), "vanilla free repair is not offered");
                MapComponent_SeaSurface seaComp = sea.GetComponent<MapComponent_SeaSurface>();
                seaComp.RebuildMask();
                ctx.Check(seaComp.lastCageCells == 24, $"the cage frame (24 edge cells) gets waterline foam in the sea mask ({seaComp.lastCageCells})");
                ctx.Check(cage.def.drawerType == DrawerType.RealtimeOnly && (Building_NetCage.FloatOffset(1f, cage.Phase, cage.SwayFactor) - Building_NetCage.FloatOffset(3f, cage.Phase, cage.SwayFactor)).magnitude > 0.001f, "the cage is drawn every frame and bobs");
                Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), false);
                foreach (Wait w in ctx.ScreenshotOn(sea, spot.CenterCell, 9f, "netcage_sea", 30)) { yield return w; }
                yield return new WaitFrames(40); // 截圖晚一幀才寫出：別在寫出前就拆掉地圖
            }
            finally
            {
                ResetDebug();
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }

        public static IEnumerable<Wait> NC8_EscapedFish(TestContext ctx)
        {
            Map home = DepthRimFixture.Home;
            MapComponent_Aquaculture.debugNoEvents = true;
            Bay bay = MakeBay(ctx, home, TerrainDefOf.WaterOceanShallow, TerrainDefOf.WaterOceanShallow);
            try
            {
                Building_NetCage cage = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, home, bay.cage);
                CompNetCage comp = cage.Cage;
                AquacultureSpeciesDef sp = FittingSpecies(comp, s => s.coastal) ?? DefDatabase<AquacultureSpeciesDef>.GetNamed("DepthRim_Aquaculture_Cod");
                comp.Stock(sp, sp.stockCount);
                List<Pawn> before = home.mapPawns.AllPawnsSpawned.Where(CompFishCatch.IsAquatic).ToList();
                comp.Breach(0.15f);
                List<Pawn> escaped = home.mapPawns.AllPawnsSpawned.Where(p => CompFishCatch.IsAquatic(p) && !before.Contains(p)).ToList();
                ctx.Check(escaped.Count >= 1 && escaped.Count <= 10, $"escaped fish appear in the bay ({escaped.Count})");
                ctx.Check(escaped.All(p => p.TryGetComp<CompFishCatch>().Variant == sp.variant), "with the farmed variant's colour and name");
                ctx.Note("spawned on: " + string.Join(", ", escaped.Select(p => home.terrainGrid.TerrainAt(p.Position).defName + (p.Position.Standable(home) ? "" : "(blocked)"))));
                yield return new WaitTicks(260);
                MapComponent_Flooding fl = home.GetComponent<MapComponent_Flooding>();
                ctx.Note($"flooding active {fl?.Active}, holds {fl?.HoldsWater}");
                foreach (Pawn p in escaped.Take(3))
                {
                    ctx.Note($"t+260 fish at {p.Position} {(p.Spawned ? home.terrainGrid.TerrainAt(p.Position).defName : "-")} onWater {FishHabitat.OnSurfaceWater(p)} sev {p.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_FishOutOfWater)?.Severity} job {p.CurJobDef?.defName}");
                }
                yield return new WaitTicks(1240);
                foreach (Pawn p in escaped)
                {
                    string where = p.Spawned ? home.terrainGrid.TerrainAt(p.Position).defName : (p.Corpse?.Spawned == true ? home.terrainGrid.TerrainAt(p.Corpse.Position).defName : "-");
                    ctx.Note($"fish {p.LabelShort}: dead {p.Dead}, spawned {p.Spawned}, at {where}, job {p.CurJobDef?.defName}, hediffs [{string.Join(", ", p.health.hediffSet.hediffs.Select(h => h.def.defName))}]");
                }
                ctx.Note($"after 1500 ticks: {escaped.Count(p => p.Spawned)} spawned, {escaped.Count(p => p.Dead)} dead, tracked {home.GetComponent<MapComponent_EscapedFish>().Tracked}");
                ctx.Check(escaped.Where(p => p.Spawned).All(p => !p.Dead && !p.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_FishOutOfWater)), "on shallow sea water they stay alive");
                ctx.Check(home.GetComponent<MapComponent_EscapedFish>().Tracked > 0, "they stay to be hunted for two days");
                foreach (Pawn p in escaped.Where(p => p.Spawned).ToList()) { p.Destroy(); }
            }
            finally
            {
                ResetDebug();
                bay.Restore();
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NC5：外海深度與水溫
        // ══════════════════════════════════════════════════════════════

        public static IEnumerable<Wait> NC5_Depth(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            PlanetTile below = OceanWorldView.TileBelow(sea.Tile);
            float elevation = below.Valid ? below.Tile.elevation : 0f;
            try
            {
                yield return new WaitFrames(2);
                MapComponent_Aquaculture aqua = MapComponent_Aquaculture.For(sea);
                ctx.Require(aqua.TryOceanBelow(out OceanTemperature.Kind kind, out float depth), "the sea map has an ocean tile below");
                ctx.Note($"ocean below: {kind}, {depth:F0} m");
                below.Tile.elevation = -1000f;
                CellRect spot = SeaCageSpot(sea, sea.Center);
                foreach (ThingDef def in new[] { DepthRimDefOf.DepthRim_NetCage_Shallow, DepthRimDefOf.DepthRim_NetCage_Midwater, DepthRimDefOf.DepthRim_NetCage_Deep })
                {
                    CompProperties_NetCage p = def.GetCompProperties<CompProperties_NetCage>();
                    ctx.Check(PlaceWorker_NetCage.Check(p, spot, sea).Accepted, $"{def.label} fits over 1000 m of water");
                    float t = aqua.WaterTemperatureAt(p.netDepth);
                    float exp = NetCageMath.OpenSeaWaterTemperature(p.netDepth, aqua.AirAnnual, sea.mapTemperature.OutdoorTemp, kind);
                    ctx.Check(Mathf.Abs(t - exp) < 0.01f, $"{def.label}: water {t:F1} °C follows the depth model");
                    List<AquacultureSpeciesDef> ok = DefDatabase<AquacultureSpeciesDef>.AllDefs.Where(s => s.CannotStockReason(CageVenue.OpenSea, p.layer, t) == null).ToList();
                    ctx.Note($"{def.label} at {t:F1} °C can farm: {string.Join(", ", ok.Select(s => s.label))}");
                    ctx.Check(ok.All(s => s.netLayers.Contains(p.layer) && s.TemperatureDistance(t) <= 2f), $"{def.label}: only species for this layer and water are offered");
                }
                below.Tile.elevation = -300f;
                AcceptanceReport deep = PlaceWorker_NetCage.Check(DepthRimDefOf.DepthRim_NetCage_Deep.GetCompProperties<CompProperties_NetCage>(), spot, sea);
                ctx.Check(!deep.Accepted && deep.Reason.Contains("300"), $"a deep net over 300 m is refused with the reason ({deep.Reason})");
                ctx.Check(PlaceWorker_NetCage.Check(DepthRimDefOf.DepthRim_NetCage_Midwater.GetCompProperties<CompProperties_NetCage>(), spot, sea).Accepted, "a thermocline net still fits over 300 m");
            }
            finally
            {
                if (below.Valid) { below.Tile.elevation = elevation; }
                ResetDebug();
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NC6：存讀檔與效能
        // ══════════════════════════════════════════════════════════════

        public static IEnumerable<Wait> NC6_SaveAndPerformance(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            MapComponent_Aquaculture.debugNoEvents = true;
            try
            {
                yield return new WaitFrames(2);
                List<Building_NetCage> cages = new List<Building_NetCage>();
                for (int i = 0; i < 12; i++)
                {
                    IntVec3 center = new IntVec3(30 + (i % 6) * 12, 0, 40 + (i / 6) * 16);
                    CellRect spot = SeaCageSpot(sea, center);
                    Building_NetCage cage = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, spot);
                    cage.Cage.Stock(DefDatabase<AquacultureSpeciesDef>.GetNamed("DepthRim_Aquaculture_Cod"), 120);
                    cage.Cage.AddFeed(200f);
                    cages.Add(cage);
                    if (i < 6)
                    {
                        Building a = (Building)DepthRimFixture.Spawn(DepthRimDefOf.DepthRim_Aerator, new IntVec3(center.x, 0, spot.minZ - 1), sea);
                        a.GetComp<CompPowerTrader>().PowerOn = true;
                    }
                }
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < 20; i++) { foreach (Building_NetCage c in cages) { c.Cage.Advance(250); } }
                double ms = sw.Elapsed.TotalMilliseconds / 20;
                ctx.Check(ms < 1.0, $"12 cages cost {ms:F3} ms per 250-tick update");

                // ── Scribe 往返 ──
                CompNetCage comp = cages[0].Cage;
                comp.SetDisease(0.4f);
                comp.FeedRateSetting = 0.5f;
                (string sp, int count, float w, float feed, float dis, float rate) snap = (comp.Species.defName, comp.Count, comp.AvgWeight, comp.FeedStore, comp.DiseaseSeverity, comp.FeedRateSetting);
                string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "depthrim_netcage_test.xml");
                Scribe.saver.InitSaving(path, "netcage");
                comp.PostExposeData();
                Scribe.saver.FinalizeSaving();
                comp.ClearFish();
                comp.FeedRateSetting = 1f;
                Scribe.loader.InitLoading(path);
                comp.PostExposeData();
                Scribe.loader.FinalizeLoading();
                File.Delete(path);
                ctx.Check(comp.Species?.defName == snap.sp && comp.Count == snap.count && Mathf.Abs(comp.AvgWeight - snap.w) < 0.0001f && Mathf.Abs(comp.FeedStore - snap.feed) < 0.01f
                    && Mathf.Abs(comp.DiseaseSeverity - snap.dis) < 0.0001f && comp.FeedRateSetting == snap.rate, "cage data survives a Scribe round trip");

                // ── 平台層看得到箱網（快照）──
                Building_PillarFoot foot = Tests_SeaSurface.BuildPillar(sea, new IntVec3(30, 0, 80));
                yield return new WaitFrames(3);
                ctx.Check(SeaLevels.PlatformLevelOf(sea) != null, "a platform level exists above the cages");
            }
            finally
            {
                ResetDebug();
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NC7：病害與擴散
        // ══════════════════════════════════════════════════════════════

        public static IEnumerable<Wait> NC7_Disease(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            MapComponent_Aquaculture.debugNoEvents = true;
            try
            {
                yield return new WaitFrames(2);
                IntVec3 c = sea.Center;
                AquacultureSpeciesDef cod = DefDatabase<AquacultureSpeciesDef>.GetNamed("DepthRim_Aquaculture_Cod");
                List<Building_NetCage> row = new List<Building_NetCage>();
                for (int i = -1; i <= 1; i++)
                {
                    CellRect spot = SeaCageSpot(sea, c + new IntVec3(i * 8, 0, 0));
                    row.Add(SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, spot));
                }
                Building_NetCage isolated = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, SeaCageSpot(sea, c + new IntVec3(0, 0, 30)));
                foreach (Building_NetCage cage in row.Append(isolated)) { cage.Cage.Stock(cod, 120); }
                MapComponent_Aquaculture aqua = MapComponent_Aquaculture.For(sea);
                row[1].Cage.Infect();
                row[1].Cage.LastSpreadCheck = -9999999;
                int count0 = row[1].Cage.Count;
                float w0 = row[1].Cage.AvgWeight;
                DiseaseSpread.debugAlwaysInfect = true;
                aqua.DailyDisease();
                DiseaseSpread.debugAlwaysInfect = false;
                ctx.Check(row[0].Cage.DiseaseSeverity > 0f && row[2].Cage.DiseaseSeverity > 0f, "the disease spreads to both neighbours");
                ctx.Check(isolated.Cage.DiseaseSeverity == 0f, "the cage 30 cells away stays healthy");
                ctx.Check(row[1].Cage.Count == count0 && row[1].Cage.AvgWeight == w0, "the disease itself kills nothing");
                ctx.Check(new Alert_CageDisease().GetReport().active, "disease alert");

                isolated.Cage.Infect();
                for (int d = 0; d < 10; d++) { aqua.DailyDisease(); }
                float s10 = isolated.Cage.DiseaseSeverity;
                for (int d = 0; d < 5; d++) { aqua.DailyDisease(); }
                ctx.Check(isolated.Cage.DiseaseSeverity < s10, $"an isolated cage starts healing after 10 days ({s10:F2} -> {isolated.Cage.DiseaseSeverity:F2})");

                // 治療（工作）
                Building_NetCage sick = row[1];
                Pawn doctor = DepthRimFixture.SpawnColonist(sea, sick.AccessCell());
                Thing med = DepthRimFixture.Spawn(ThingDefOf.MedicineIndustrial, sick.AccessCell() + IntVec3.South, sea);
                med.stackCount = 10;
                float before = sick.Cage.DiseaseSeverity;
                WorkGiver_TreatCage giver = new WorkGiver_TreatCage();
                for (int i = 0; i < 3 && sick.Cage.DiseaseSeverity == before; i++)
                {
                    Job job = giver.JobOnThing(doctor, sick);
                    if (job == null) { break; }
                    foreach (Wait w in RunJob(ctx, doctor, job, "treatment step " + i)) { yield return w; }
                }
                ctx.Check(sick.Cage.DiseaseSeverity <= Mathf.Max(0f, before - 0.39f), $"treatment lowers severity by 0.4 ({before:F2} -> {sick.Cage.DiseaseSeverity:F2})");
                row[0].Cage.Harvest(row[0].AccessCell());
                ctx.Check(row[0].Cage.DiseaseSeverity == 0f && !row[0].Cage.HasFish, "clearing a cage removes the disease");
            }
            finally
            {
                ResetDebug();
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NC9／NC10：原版氣候事件與冷暖洋流
        // ══════════════════════════════════════════════════════════════

        public static IEnumerable<Wait> NC9_VanillaClimate(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            MapComponent_Aquaculture.debugNoEvents = true;
            try
            {
                yield return new WaitFrames(2);
                Building_NetCage cage = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, SeaCageSpot(sea, sea.Center));
                CompNetCage comp = cage.Cage;
                MapComponent_Aquaculture aqua = MapComponent_Aquaculture.For(sea);
                comp.Advance(250);
                float baseO2 = comp.Environment.oxygenExtra;
                (GameConditionDef def, float o2, float growth)[] cases =
                {
                    (GameConditionDefOf.HeatWave, -0.10f, 1f), (GameConditionDefOf.ColdSnap, 0.05f, 1f),
                    (GameConditionDefOf.VolcanicWinter, 0f, 0.85f), (GameConditionDefOf.ToxicFallout, -0.05f, 0.85f)
                };
                foreach (var cs in cases)
                {
                    GameCondition cond = GameConditionMaker.MakeCondition(cs.def, GenDate.TicksPerDay);
                    sea.gameConditionManager.RegisterCondition(cond);
                    comp.Advance(250);
                    float expectedTemp = aqua.WaterTemperatureAt(comp.Props.netDepth);
                    ctx.Check(Mathf.Abs(comp.WaterTemp - expectedTemp) < 0.01f, $"{cs.def.defName}: water temperature only follows the outdoor temperature ({comp.WaterTemp:F1} °C)");
                    ctx.Check(Mathf.Abs(comp.Environment.oxygenExtra - baseO2 - cs.o2) < 0.001f, $"{cs.def.defName}: oxygen {cs.o2:+0.00;-0.00}");
                    ctx.Check(Mathf.Abs(aqua.VanillaClimate.growth - cs.growth) < 0.001f, $"{cs.def.defName}: growth x{cs.growth}");
                    cond.End();
                    yield return new WaitTicks(1);
                }
            }
            finally
            {
                ResetDebug();
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }

        public static IEnumerable<Wait> NC10_OceanCurrents(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            MapComponent_Aquaculture.debugNoEvents = true;
            Map home = DepthRimFixture.Home;
            try
            {
                yield return new WaitFrames(2);
                Building_NetCage cage = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, SeaCageSpot(sea, sea.Center));
                cage.Cage.Stock(DefDatabase<AquacultureSpeciesDef>.GetNamed("DepthRim_Aquaculture_Cod"), 120);
                MapComponent_Aquaculture aqua = MapComponent_Aquaculture.For(sea);
                ctx.Check(aqua.TryFire(DepthRimDefOf.DepthRim_WarmCurrentIncident), "the warm current incident fires on a sea map with a stocked cage");
                ctx.Check(aqua.Current == OceanCurrentKind.Warm, "the map has a warm current");
                float[] depths = { 10f, 150f, 500f };
                float[] offsets = { 3f, 1.4f, 0f };
                for (int i = 0; i < 3; i++)
                {
                    CageEnvironment env = aqua.EnvironmentFor(cage, depths[i], new FloatRange(-2f, 12f));
                    ctx.Check(Mathf.Abs(env.waterTemp - aqua.WaterTemperatureAt(depths[i]) - offsets[i]) < 0.01f, $"warm current at {depths[i]} m: +{offsets[i]} °C");
                }
                ctx.Check(Mathf.Abs(aqua.EnvironmentFor(cage, 10f, new FloatRange(-2f, 12f)).oxygenExtra - aqua.VanillaClimate.oxygen + 0.05f) < 0.001f, "warm current: oxygen -0.05");
                ctx.Check(ClimateMath.EventFactor(aqua.Current) == ClimateMath.CurrentEventFactor, "ocean current turbines get x1.15");
                ctx.Check(!aqua.TryFire(DepthRimDefOf.DepthRim_ColdCurrentIncident), "only one current at a time");
                ctx.Check(!MapComponent_Aquaculture.For(home).TryFire(DepthRimDefOf.DepthRim_ColdCurrentIncident), "no currents in a bay");
                ctx.Check(MapComponent_Aquaculture.For(home).Current == OceanCurrentKind.None, "the bay has no current");
            }
            finally
            {
                ResetDebug();
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NC11：水中魚影；NC12：掠食者
        // ══════════════════════════════════════════════════════════════

        public static IEnumerable<Wait> NC11_FishShadows(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            MapComponent_Aquaculture.debugNoEvents = true;
            try
            {
                yield return new WaitFrames(2);
                MapComponent_Aquaculture aqua = MapComponent_Aquaculture.For(sea);
                Current.Game.CurrentMap = sea;
                aqua.SpawnFishShadows();
                ctx.Check(aqua.FishShadowsSpawned == 0, "no fish shadows without stocked cages");
                for (int i = -1; i <= 1; i++)
                {
                    Building_NetCage cage = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, SeaCageSpot(sea, sea.Center + new IntVec3(i * 10, 0, 0)));
                    cage.Cage.Stock(DefDatabase<AquacultureSpeciesDef>.GetNamed("DepthRim_Aquaculture_Cod"), 120);
                    cage.Cage.AddFeed(100f);
                }
                int pawns = sea.mapPawns.AllPawnsSpawnedCount;
                ctx.Check(Mathf.Abs(aqua.FishDensity() - 1f) < 0.001f, $"three fed cages: density 1 ({aqua.FishDensity():F2})");
                for (int i = 0; i < 20; i++) { aqua.SpawnFishShadows(); }
                ctx.Check(aqua.FishShadowsSpawned > 0 && aqua.FishShadowsSpawned <= FishShadowField.MaxFlecks, $"fish shadows appear around the cages, capped ({aqua.FishShadowsSpawned})");
                ctx.Check(sea.mapPawns.AllPawnsSpawnedCount == pawns, "no pawns are spawned for them");
                Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), false);
                foreach (Wait w in ctx.ScreenshotOn(sea, sea.Center, 20f, "netcage_shadows", 30)) { yield return w; }
                yield return new WaitFrames(40); // 截圖晚一幀才寫出：別在寫出前就拆掉地圖
            }
            finally
            {
                ResetDebug();
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }

        public static IEnumerable<Wait> NC12_Predator(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            MapComponent_Aquaculture.debugNoEvents = true;
            try
            {
                yield return new WaitFrames(2);
                Building_NetCage cage = SpawnCage(DepthRimDefOf.DepthRim_NetCage_Shallow, sea, SeaCageSpot(sea, sea.Center));
                cage.Cage.Stock(DefDatabase<AquacultureSpeciesDef>.GetNamed("DepthRim_Aquaculture_Cod"), 120);
                MapComponent_Aquaculture aqua = MapComponent_Aquaculture.For(sea);
                ctx.Check(aqua.TryFire(DepthRimDefOf.DepthRim_CagePredator), "the predator incident fires");
                Pawn shark = sea.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.kindDef == DepthRimDefOf.DepthRim_SleeperShark);
                ctx.Require(shark != null, "a sleeper shark appears on the open sea");
                CompNetRaider raider = shark.TryGetComp<CompNetRaider>();
                ctx.Check(raider != null && raider.Active && raider.Target == cage, "it goes for the cage");
                ctx.Check(shark.TryGetComp<CompFishCatch>().Variant?.defName == "DepthRim_FishVariant_SleeperShark", "with the sleeper shark look");
                int hp = cage.HitPoints;
                yield return new WaitUntil(() => cage.HitPoints < hp || !shark.Spawned, 3000);
                ctx.Check(cage.HitPoints < hp, $"it bites the net ({hp} -> {cage.HitPoints})");
                yield return new WaitUntil(() => !raider.Active, 30000);
                ctx.Check(!raider.Active && hp - cage.HitPoints <= 80, $"then it leaves ({hp - cage.HitPoints} damage)");

                // 灣內的水獺：野生的不咬網
                PawnKindDef otter = DefDatabase<PawnKindDef>.GetNamedSilentFail("Otter");
                if (otter != null)
                {
                    Pawn wild = PawnGenerator.GeneratePawn(otter);
                    ctx.Check(wild.TryGetComp<CompNetRaider>() != null && !wild.TryGetComp<CompNetRaider>().Active, "Odyssey otters carry an inactive net-raider comp");
                    Find.WorldPawns.PassToWorld(wild, PawnDiscardDecideMode.Discard);
                }
            }
            finally
            {
                ResetDebug();
                foreach (Pawn p in sea.mapPawns.AllPawnsSpawned.Where(p => p.kindDef == DepthRimDefOf.DepthRim_SleeperShark).ToList()) { p.Destroy(); }
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
        }
    }
}
