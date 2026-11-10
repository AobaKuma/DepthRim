using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>潛艇：DMSE 轉移飛行在海洋層上的完整流程。</summary>
    public static class Tests_Submarine
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>
        /// S2：在冰洋測試地圖上挖一個深淵坑當船塢，鋪 9×9 的船體平台、蓋龍骨框架、艦橋、推進器、充滿電的電池，
        /// 放一疊鋼材當貨物；規劃到兩格外海洋格的航程並起飛（略過殖民者操作，直接開始倒數）。
        /// 檢查：分離出的父物件是潛艇、船體與貨物在潛艇地圖上、船塢留下深淵（沒有太空地形）、
        /// 航線只走海洋格、潛艇地圖的水深層級由潛艇提供、下潛規則（海床與船殼耐壓）。
        /// </summary>
        public static IEnumerable<Wait> S2_SubmarineVoyage(TestContext ctx) => Voyage(ctx, anchor: false);

        /// <summary>
        /// S4：同 S2 的船塢與起飛，然後檢查和 DMSE 不同的航行推進：推進器運作時航程前進；關掉推進器後潛艇停在海中、
        /// 航程不再前進；重新打開後繼續；最後就地停船，停在目前所在的格子，之後能重新規劃航程。
        /// </summary>
        public static IEnumerable<Wait> S4_SubmarineThrust(TestContext ctx) => Voyage(ctx, anchor: false, thrust: true);

        /// <summary>
        /// S3：同 S2 的船塢與起飛，然後讓潛艇視為已抵達並下錨：深度不對時不能下錨；下錨在旁邊一格建立海洋地點、
        /// 生成海床地圖、船體自動降落進去、潛艇世界物件移除；平台蓋到海床的格子記下原本的地形，
        /// 那格變回深淵（離港或拆平台）時海床會還原。
        /// </summary>
        public static IEnumerable<Wait> S3_SubmarineAnchoring(TestContext ctx) => Voyage(ctx, anchor: true);

        private static IEnumerable<Wait> Voyage(TestContext ctx, bool anchor, bool thrust = false)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            ctx.Require(TransferEnvironment.AllowsTransfer(map), "the ocean layer allows transfers (DMSE extension)");
            ctx.Check(TransferEnvironment.VoidTerrain(map) == DepthRimDefOf.DepthRim_Abyss, "the ocean's void is the abyss");

            // ── 船塢：深淵坑 + 9×9 船體平台 ──
            TerrainDef abyss = DepthRimDefOf.DepthRim_Abyss;
            TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            Dictionary<IntVec3, TerrainDef> before = map.AllCells.ToDictionary(c => c, c => map.terrainGrid.TerrainAt(c));
            int pocketCells = GenStep_AbyssCarve.CarvePockets(map, 2, new FloatRange(12f, 12f));
            ctx.Note($"carved {pocketCells} pocket cells");
            CellRect deck = CellRect.Empty;
            foreach (IntVec3 c in map.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 9, 9);
                if (r.ExpandedBy(1).InBounds(map) && r.ExpandedBy(1).Cells.All(n => map.terrainGrid.TerrainAt(n) == abyss))
                {
                    deck = r;
                    break;
                }
            }
            ctx.Require(deck.Area > 0, "an abyss pocket big enough for a 9x9 shipyard deck");
            foreach (IntVec3 c in deck.ExpandedBy(3))
            {
                if (c.InBounds(map)) { map.fogGrid.Unfog(c); }
            }
            foreach (IntVec3 c in deck)
            {
                map.terrainGrid.SetTerrain(c, platform);
            }
            ctx.Note($"shipyard deck {deck}");

            IntVec3 center = deck.CenterCell;
            List<Thing> built = new List<Thing>();
            // 電線先鋪（陣營在生成前設好）：之後生成的用電建築才會接到電池所在的電網
            foreach (IntVec3 c in deck)
            {
                Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                conduit.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(conduit, c, map);
            }
            Thing Build(string def, IntVec3 at, Rot4 rot)
            {
                Thing t = ThingMaker.MakeThing(ThingDef.Named(def), ThingDef.Named(def).MadeFromStuff ? ThingDefOf.Steel : null);
                t.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(t, at, map, rot);
                built.Add(t);
                return t;
            }
            Thing keel = Build("DepthRim_Keel", center, Rot4.North);
            Thing helm = Build("DepthRim_Helm", center + new IntVec3(-2, 0, 3), Rot4.North);
            Thing thruster = Build("DepthRim_Thruster", center + new IntVec3(3, 0, -2), Rot4.North);
            Thing battery = Build("Battery", center + new IntVec3(-3, 0, -3), Rot4.North);
            Thing cargo = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), center + new IntVec3(0, 0, 0) + new IntVec3(1, 0, -3), map);
            cargo.stackCount = 37;
            battery.TryGetComp<CompPowerBattery>().AddEnergy(10000f);
            yield return new WaitTicks(5);
            battery.TryGetComp<CompPowerBattery>().AddEnergy(10000f);
            MapComponent_PileNetwork.For(map).MarkDirty();
            yield return new WaitTicks(5);

            // 原版電網一次只隨機打開一個等電的建築（兩個時每 100 ticks 一個），等到都接上電
            CompPowerTrader helmPower = helm.TryGetComp<CompPowerTrader>();
            CompPowerTrader thrusterPower = thruster.TryGetComp<CompPowerTrader>();
            yield return new WaitUntil(() => helmPower.PowerOn && thrusterPower.PowerOn, 1200);
            CompTransferConsole console = helm.TryGetComp<CompTransferConsole>();
            ctx.Require(console != null, "the helm is a transfer console");
            ctx.Check(helm.TryGetComp<CompPowerTrader>().PowerOn, "the helm is powered by the battery");
            ctx.Check(thruster.TryGetComp<CompPowerTrader>().PowerOn, "the thruster is powered by the battery");
            PileNetwork net = console.Network;
            ctx.Require(net != null, "the helm stands on the keel network");
            net.RecalculateFootprint();
            ctx.Check(net.Footprint.All(c => map.terrainGrid.TerrainAt(c) == platform), "the hull footprint is hull platform only (no seabed)");
            ctx.Check(net.Footprint.Count == deck.Area, $"the whole deck is hull ({net.Footprint.Count} cells)");
            TransferShipSurvey survey = net.Survey;
            ctx.Note($"survey: engines {survey.operationalEngines}, TWR {survey.TWR:0.##}, fuel {survey.totalFuel:0}");
            ctx.Check(survey.operationalEngines == 1, "the thruster counts as an engine");
            ctx.Check(survey.totalFuel >= 500f, $"battery charge counts as fuel ({survey.totalFuel:0})");

            // ── 目的地：海路 2～4 格外、沒有據點的海洋格 ──
            // 測試用的冰洋地點是夾具硬設成極地冰蓋的一格（周圍多半是陸塊），先把附近三圈也設成極地冰蓋，結束時還原
            PlanetTile origin = map.Tile;
            Dictionary<PlanetTile, BiomeDef> biomeBefore = new Dictionary<PlanetTile, BiomeDef>();
            BiomeDef polar = DefDatabase<BiomeDef>.GetNamed("DepthRim_PolarIceCap");
            List<PlanetTile> frontier = new List<PlanetTile> { origin };
            for (int ringIndex = 0; ringIndex < 3; ringIndex++)
            {
                List<PlanetTile> next = new List<PlanetTile>();
                foreach (PlanetTile t in frontier)
                {
                    List<PlanetTile> nb = new List<PlanetTile>();
                    Find.WorldGrid.GetTileNeighbors(t, nb);
                    foreach (PlanetTile n in nb)
                    {
                        if (n == origin || biomeBefore.ContainsKey(n)) { continue; }
                        biomeBefore[n] = n.Tile.PrimaryBiome;
                        n.Tile.PrimaryBiome = polar;
                        next.Add(n);
                    }
                }
                frontier = next;
            }
            OceanRouting.ClearCache();
            PlanetTile dest = PlanetTile.Invalid;
            int navigableNeighbors = 0;
            List<PlanetTile> ring = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(origin, ring);
            navigableNeighbors = ring.Count(OceanRouting.Navigable);
            ctx.Note($"origin {origin} ({origin.Tile?.PrimaryBiome?.defName}), navigable neighbours {navigableNeighbors}/{ring.Count}");
            for (int i = 0; i < origin.Layer.TilesCount && !dest.Valid; i++)
            {
                PlanetTile t = new PlanetTile(i, origin.Layer);
                int d = OceanRouting.Distance(origin, t);
                if (d >= 2 && d <= 4 && !Find.WorldObjects.AnyWorldObjectAt(t))
                {
                    dest = t;
                }
            }
            ctx.Require(dest.Valid, "found an open ocean tile two to four cells away by sea");
            ctx.Note($"destination {dest} ({dest.Tile?.PrimaryBiome?.defName}), {OceanRouting.Distance(origin, dest)} tiles by sea");
            List<PlanetTile> route = WorldObject_TransferShip.BuildPath(origin, dest);
            ctx.Check(route.All(OceanRouting.Navigable), "the route only crosses ocean tiles");
            ctx.Check(TransferPlanningUtility.ValidateDestination(origin, dest, 99, false), "the destination is valid");
            PlanetTile land = PlanetTile.Invalid;
            for (int i = 0; i < origin.Layer.TilesCount; i++)
            {
                PlanetTile t = new PlanetTile(i, origin.Layer);
                if (!OceanRouting.Navigable(t)) { land = t; break; }
            }
            if (land.Valid)
            {
                ctx.Check(!TransferPlanningUtility.ValidateDestination(origin, land, 9999, false), "dry land is not a valid destination");
            }

            // ── 航程耗電：規劃估算以推進器功率計，出航不預扣 ──
            CompPowerBattery batteryComp = battery.TryGetComp<CompPowerBattery>();
            float thrusterWatts = thrusterPower.Props.PowerConsumption;
            yield return new WaitTicks(260);
            ctx.Check(Mathf.Abs(thrusterPower.PowerOutput + thrusterWatts * VoyageMath.StandbyFactor) < 0.5f, $"in the shipyard the thruster idles at standby ({thrusterPower.PowerOutput:0.#} W)");
            net.InvalidateSurvey();
            survey = net.Survey;
            int seaDistance = OceanRouting.Distance(origin, dest);
            TransferEstimate estimate = TransferEstimate.For(survey, seaDistance);
            float expectNeed = VoyageMath.EnergyWd(thrusterWatts, estimate.totalHours);
            ctx.Note($"estimate: {estimate.totalHours:0.#} h, need {estimate.fuelRequired:0.#} Wd of {estimate.fuelAvailable:0} Wd, range {estimate.maxDistance} tiles");
            ctx.Check(Mathf.Abs(estimate.fuelRequired - expectNeed) < 0.5f, $"the energy needed is the thruster draw over the voyage ({estimate.fuelRequired:0.#} vs {expectNeed:0.#})");
            int expectRange = VoyageMath.RangeTiles(estimate.fuelAvailable, thrusterWatts, estimate.burnHours + estimate.arrivalBurnHours, TransferFlightMath.Tuning.coastHoursPerTile);
            ctx.Check(estimate.maxDistance == Mathf.Min(expectRange, Patch_SubmarineEnergy_Estimate.MaxRangeTiles), $"the range is how far the batteries can run the thrusters ({estimate.maxDistance} vs {expectRange})");
            ctx.Check(estimate.maxDistance >= seaDistance, "the destination is within battery range");
            float chargeBefore = batteryComp.StoredEnergy;

            // ── 起飛 ──
            console.SetPlan(new TransferPlan(origin, dest, false));
            string blocked = console.LaunchBlockedReason();
            ctx.Require(blocked == null, $"ready to cast off ({blocked})");
            console.BeginLaunchSequence();
            console.BeginCountdown();
            ctx.Check(console.State == TransferConsoleState.Countdown, "countdown started");
            WorldObject_Submarine sub = null;
            yield return new WaitUntil(() => (sub = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>().FirstOrDefault(s => s.HasMap)) != null, 3000);
            ctx.Require(sub != null, "a submarine world object was created");
            Map subMap = sub.Map;
            ctx.Check(sub.def.defName == "DepthRim_Submarine", "the ship world object is the DepthRim submarine");
            ctx.Check(sub.InFlight, $"the submarine is under way ({sub.phase})");
            ctx.Check(chargeBefore - batteryComp.StoredEnergy < 20f, $"casting off draws no voyage charge up front ({chargeBefore:0} -> {batteryComp.StoredEnergy:0} Wd)");
            ctx.Check(helm.Map == subMap && thruster.Map == subMap && keel.Map == subMap && battery.Map == subMap, "the hull buildings moved to the submarine map");
            ctx.Check(cargo.Map == subMap && cargo.Position == center + new IntVec3(1, 0, -3), "cargo kept its position aboard");
            ctx.Check(deck.Cells.All(c => subMap.terrainGrid.TerrainAt(c) == platform), "the deck is on the submarine map at the same coordinates");
            ctx.Check(subMap.AllCells.Where(c => !deck.Contains(c)).All(c => subMap.terrainGrid.TerrainAt(c) == abyss), "everything outside the hull is abyss");
            ctx.Check(deck.Cells.All(c => map.terrainGrid.TerrainAt(c) == abyss), "the shipyard is open abyss again");
            ctx.Check(!map.AllCells.Any(c => map.terrainGrid.TerrainAt(c) == TerrainDefOf.Space), "no space terrain on the shipyard map");

            if (thrust)
            {
                // ── 推進器要一直運作才會前進 ──
                yield return new WaitTicks(120);
                ctx.Check(sub.plannedThrust > 0f && sub.ThrustFraction >= 0.99f, $"under way at full thrust ({sub.currentThrust}/{sub.plannedThrust})");
                float p0 = sub.Progress;
                yield return new WaitTicks(900);
                float p1 = sub.Progress;
                ctx.Check(p1 > p0, $"the voyage advances while the thruster runs ({p0:0.0000} -> {p1:0.0000})");
                ctx.Check(Mathf.Abs(thrusterPower.PowerOutput + thrusterWatts) < 0.5f, $"under way the thruster draws full power ({thrusterPower.PowerOutput:0.#} W)");

                CompFlickable flick = thruster.TryGetComp<CompFlickable>();
                flick.SwitchIsOn = false;
                yield return new WaitTicks(VoyageMath.ThrustCheckInterval * 2 + 10);
                ctx.Check(sub.Stalled, $"with the thruster off the submarine drifts in place (thrust {sub.currentThrust})");
                float p2 = sub.Progress;
                yield return new WaitTicks(900);
                float p3 = sub.Progress;
                ctx.Check(p3 - p2 < (p1 - p0) * 0.1f + 1e-5f, $"no progress while stalled ({p2:0.0000} -> {p3:0.0000})");
                ctx.Check(sub.InFlight, "a stalled submarine is still on its voyage");

                flick.SwitchIsOn = true;
                yield return new WaitUntil(() => !sub.Stalled, 1200);
                ctx.Check(!sub.Stalled, "the thruster powers back up and the voyage resumes");
                float p4 = sub.Progress;
                yield return new WaitTicks(900);
                ctx.Check(sub.Progress > p4, $"progress resumes ({p4:0.0000} -> {sub.Progress:0.0000})");

                // 就地停船
                PlanetTile here = sub.Tile;
                sub.StopHere();
                ctx.Check(!sub.InFlight && sub.phase == TransferPhase.Arrived, "stopping ends the voyage");
                ctx.Check(sub.Tile == here && sub.destination == here, "the submarine stays on the tile it was on");
                yield return new WaitTicks(260);
                ctx.Check(!sub.Destroyed && sub.HasMap && helm.Map == subMap, "the submarine and its crew stay at sea");
                ctx.Check(Mathf.Abs(thrusterPower.PowerOutput + thrusterWatts * VoyageMath.StandbyFactor) < 0.5f, $"after stopping the thruster drops back to standby ({thrusterPower.PowerOutput:0.#} W)");
                CompTransferConsole subConsole = helm.TryGetComp<CompTransferConsole>();
                ctx.Check(!TransferReadiness.Check(subConsole, null, includeWarnings: false).Any(i => i.IsError && i.text == "DMSE.Transfer.Issue.InFlight".Translate()), "a new voyage can be planned from where it stopped");

                Current.Game.CurrentMap = map;
                Current.Game.DeinitAndRemoveMap(subMap, false);
                if (!sub.Destroyed) { sub.Destroy(); }
                foreach (KeyValuePair<IntVec3, TerrainDef> kv in before)
                {
                    if (map.terrainGrid.TerrainAt(kv.Key) != kv.Value)
                    {
                        map.terrainGrid.SetTerrain(kv.Key, kv.Value);
                    }
                }
                foreach (KeyValuePair<PlanetTile, BiomeDef> kv in biomeBefore)
                {
                    kv.Key.Tile.PrimaryBiome = kv.Value;
                }
                OceanRouting.ClearCache();
                yield return Updates(1);
                yield break;
            }

            if (anchor)
            {
                // ── 下錨 ──
                // 航程要好幾個小時：測試直接把潛艇視為已抵達
                sub.phase = TransferPhase.Arrived;
                yield return new WaitTicks(5);
                DepthTier floor = sub.Seafloor;
                ctx.Note($"arrived over {sub.Tile} ({floor})");
                DepthTier savedCruise = sub.cruiseTier;
                sub.cruiseTier = sub.targetTier = floor == DepthTier.Shallow ? DepthTier.Thermocline : DepthTier.Shallow;
                ctx.Check(sub.AnchorBlockedReason(out _) != null, "cannot anchor while not resting at the seafloor's depth");
                sub.cruiseTier = sub.targetTier = savedCruise;
                string anchorBlocked = sub.AnchorBlockedReason(out PlanetTile anchorage);
                ctx.Require(anchorBlocked == null, $"can anchor here ({anchorBlocked})");
                ctx.Check(WorldObject_Submarine.SeafloorAt(anchorage) == floor, "the anchorage has the same seafloor depth");
                sub.Anchor();
                MapParent site = Find.WorldObjects.MapParentAt(anchorage);
                ctx.Require(site != null && site.def == DepthRimDefOf.DepthRim_OceanSite, "anchoring created an ocean site next to the submarine");
                ctx.Check(sub.landingTarget == anchorage, "the submarine is landing at the anchorage");
                yield return new WaitUntil(() => site.HasMap, 6000);
                ctx.Require(site.HasMap, "the anchorage map was generated");
                Map seafloorMap = site.Map;
                yield return new WaitFrames(10);
                TransferLandingUtility.LandAutomatically(sub);
                yield return new WaitUntil(() => sub.Destroyed, 3000);
                ctx.Require(sub.Destroyed, "the submarine settled and its world object is gone");
                ctx.Check(helm.Map == seafloorMap && thruster.Map == seafloorMap && keel.Map == seafloorMap, "the hull is on the seafloor map");
                List<IntVec3> deckCells = seafloorMap.AllCells.Where(c => seafloorMap.terrainGrid.TerrainAt(c) == platform).ToList();
                ctx.Check(deckCells.Count == deck.Area, $"the whole deck landed ({deckCells.Count} cells)");
                MapComponent_SeabedMemory memory = seafloorMap.GetComponent<MapComponent_SeabedMemory>();
                int onSeabed = deckCells.Count(c => memory.TryGetUnder(c, out _));
                ctx.Note($"{onSeabed} of {deckCells.Count} deck cells rest on seabed");
                ctx.Check(seafloorMap.IsPlayerHome || seafloorMap.Parent.Faction == Faction.OfPlayer, "the anchorage belongs to the player");
                IntVec3 rest = deckCells.FirstOrDefault(c => memory.TryGetUnder(c, out _));
                if (rest.IsValid)
                {
                    memory.TryGetUnder(rest, out TerrainDef bed);
                    seafloorMap.terrainGrid.SetTerrain(rest, abyss);
                    ctx.Check(seafloorMap.terrainGrid.TerrainAt(rest) == bed, $"when the deck leaves, the seabed comes back ({bed.defName})");
                    seafloorMap.terrainGrid.SetTerrain(rest, platform);
                }
                Current.Game.CurrentMap = seafloorMap;
                foreach (Wait w in ctx.ScreenshotOn(seafloorMap, helm.Position, 12f, "submarine_anchored", 40)) { yield return w; }
                yield return new WaitFrames(30);

                // 收尾：移除下錨地點與它的地圖
                Current.Game.CurrentMap = map;
                Current.Game.DeinitAndRemoveMap(seafloorMap, false);
                if (!site.Destroyed) { site.Destroy(); }
                foreach (KeyValuePair<IntVec3, TerrainDef> kv in before)
                {
                    if (map.terrainGrid.TerrainAt(kv.Key) != kv.Value)
                    {
                        map.terrainGrid.SetTerrain(kv.Key, kv.Value);
                    }
                }
                foreach (KeyValuePair<PlanetTile, BiomeDef> kv in biomeBefore)
                {
                    kv.Key.Tile.PrimaryBiome = kv.Value;
                }
                OceanRouting.ClearCache();
                yield return Updates(1);
                yield break;
            }

            // ── 深度 ──
            yield return new WaitTicks(5);
            ctx.Check(FloodingUtility.DepthTierOf(subMap) == sub.CurrentDepthTier, "the submarine map's tier comes from the submarine");
            ctx.Check(subMap.Flooding()?.Active == true, "flooding runs on the submarine map");
            DepthTier seafloor = sub.Seafloor;
            ctx.Note($"seafloor {seafloor}, cruise {sub.cruiseTier}, current {sub.CurrentDepthTier}");
            ctx.Check(sub.cruiseTier == WorldObject_Submarine.SeafloorAt(origin), "it starts at the shipyard's depth");
            ctx.Check(sub.CanDiveTo(sub.targetTier) == DiveMath.DiveBlock.AlreadyThere, "the current depth is not offered again");
            if (seafloor < DepthTier.Seabed)
            {
                ctx.Check(sub.CanDiveTo(seafloor + 1) == DiveMath.DiveBlock.BelowSeafloor, "cannot dive below the seafloor here");
            }
            ctx.Check(sub.HullRating(out Building none) == HullPressure.Unlimited && none == null, "an open deck has no hull limit");

            // 朝外海的一般牆 → 船殼只算淺海；換成水密隔牆 → 撐到海床
            IntVec3 edge = new IntVec3(deck.minX, 0, deck.CenterCell.z);
            Thing wall = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), edge, subMap);
            wall.SetFaction(Faction.OfPlayer);
            yield return Updates(1);
            ctx.Check(sub.HullRating(out Building weakest) == DepthTier.Shallow && weakest == wall, "a plain wall facing the sea limits the hull to the shallows");
            wall.Destroy();
            Thing hull = GenSpawn.Spawn(ThingDef.Named("DepthRim_PressureHullWall"), edge, subMap);
            hull.SetFaction(Faction.OfPlayer);
            yield return Updates(1);
            ctx.Check(sub.HullRating(out _) == DepthTier.Seabed, "a pressure hull wall is rated to the seabed");

            Current.Game.CurrentMap = subMap;
            foreach (Wait w in ctx.ScreenshotOn(subMap, center, 9f, "submarine_under_way", 40)) { yield return w; }
            yield return new WaitFrames(30);

            // ── 收尾：拆掉潛艇與它的地圖，還原船塢地形 ──
            Current.Game.CurrentMap = map;
            Current.Game.DeinitAndRemoveMap(subMap, false);
            if (!sub.Destroyed) { sub.Destroy(); }
            foreach (KeyValuePair<IntVec3, TerrainDef> kv in before)
            {
                if (map.terrainGrid.TerrainAt(kv.Key) != kv.Value)
                {
                    map.terrainGrid.SetTerrain(kv.Key, kv.Value);
                }
            }
            foreach (KeyValuePair<PlanetTile, BiomeDef> kv in biomeBefore)
            {
                kv.Key.Tile.PrimaryBiome = kv.Value;
            }
            OceanRouting.ClearCache();
            yield return Updates(1);
            ctx.Check(!Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>().Any(), "cleaned up");
        }
    }
}
