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

        /// <summary>
        /// S5：航行演出的截圖（2026-11-11：潛艇航行動畫沿用轉移船的殘留與平台渲染問題）。同 S2 的船塢，打開過場動畫設定，
        /// 拍引擎暖機、離港過場的幾個時間點、潛艇地圖（拉遠看地圖邊緣）與世界地圖上的潛艇。
        /// </summary>
        public static IEnumerable<Wait> S5_SubmarineVoyageVisuals(TestContext ctx) => Voyage(ctx, anchor: false, visuals: true);

        /// <summary>
        /// S8：同 S2 的船塢與起飛，然後檢查航行中落海（<see cref="VoyageOverboard"/>）：在密閉艙室、沒屋頂的甲板與船外深淵各放一名殖民者。
        /// 不在航行時（視為已抵達）誰都不會失蹤；回到航行後艙外的兩人失蹤（成為世界 pawn、沒有死亡）並收到信，艙室裡的人留在船上。
        /// </summary>
        public static IEnumerable<Wait> S8_VoyageOverboard(TestContext ctx) => Voyage(ctx, anchor: false, overboard: true);

        /// <summary>A1：同 S2 的船塢與起飛，然後檢查航行中的安全繩錨點（<see cref="Tests_RopeAnchor.Voyage"/>）。</summary>
        public static IEnumerable<Wait> A1_RopeAnchorVoyage(TestContext ctx) => Voyage(ctx, anchor: false, rope: true);

        private static IEnumerable<Wait> Voyage(TestContext ctx, bool anchor, bool thrust = false, bool visuals = false, bool overboard = false, bool rope = false)
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
            // 抽乾的密閉艙室（甲板角落，耐壓隔牆圍出一格、有屋頂；避開中央的龍骨）：出港、航行、下錨後都要維持乾的
            List<IntVec3> dryRoom = new List<IntVec3>();
            if (anchor || thrust || overboard || rope)
            {
                foreach (IntVec3 c in CellRect.FromLimits(center.x + 2, center.z + 2, center.x + 4, center.z + 4))
                {
                    bool ringCell = c.x == center.x + 2 || c.x == center.x + 4 || c.z == center.z + 2 || c.z == center.z + 4;
                    if (ringCell)
                    {
                        Build("DepthRim_PressureHullWall", c, Rot4.North);
                    }
                    else
                    {
                        dryRoom.Add(c);
                    }
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
                map.Flooding().MarkDry(dryRoom);
            }
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
            ctx.Check(estimate.maxDistance == Mathf.Min(expectRange, SubmarineEnergyWorker.MaxRangeTiles), $"the range is how far the batteries can run the thrusters ({estimate.maxDistance} vs {expectRange})");
            ctx.Check(estimate.maxDistance >= seaDistance, "the destination is within battery range");
            float chargeBefore = batteryComp.StoredEnergy;

            if (dryRoom.Count > 0)
            {
                float before0 = dryRoom.Max(c => map.Flooding().LevelAt(c));
                ctx.Check(before0 < 0.05f, $"the compartment is drained in the shipyard ({before0:F2})");
            }

            // ── 起飛 ──
            console.SetPlan(new TransferPlan(origin, dest, false));
            string blocked = console.LaunchBlockedReason();
            ctx.Require(blocked == null, $"ready to cast off ({blocked})");
            bool cutscenesBefore = Prefs.GravshipCutscenes;
            if (visuals)
            {
                Prefs.GravshipCutscenes = true;
                AcceptanceDriver.KeepWindowOpen = typeof(TransferTakeoffCutscene);
            }
            console.BeginLaunchSequence();
            console.BeginCountdown();
            ctx.Check(console.State == TransferConsoleState.Countdown, "countdown started");
            if (visuals)
            {
                Current.Game.CurrentMap = map;
                // 潛艇的演出（2026-11-11）：沒有火箭音、震動、噴口火花、尾焰燒傷，離港過場是潛艇自己的
                TransferPresentation presentation = TransferEnvironment.Presentation(map);
                ctx.Check(presentation != TransferPresentation.Space, "the ocean layer has its own presentation");
                ctx.Check(presentation.cameraShake == 0f && !presentation.nozzleSparks && presentation.exhaustDamage == 0f, "no camera shake, nozzle sparks or exhaust burns for a submarine");
                ctx.Check(presentation.launchSound?.defName == "DepthRim_CastOff" && presentation.thrusterLoopSound?.defName == "DepthRim_PropellerLoop", "cast-off and propeller sounds instead of the gravship's");
                ctx.Check(presentation.takeoffCutsceneClass == typeof(SubmarineTakeoffCutscene), "the submarine has its own cast-off cutscene");
                yield return new WaitUntil(() => console.State != TransferConsoleState.Countdown || console.CountdownProgress > 0.4f, 3000);
                ctx.Check(console.State == TransferConsoleState.Countdown, "still warming up for the warm-up screenshot");
                foreach (Wait w in ctx.ScreenshotOn(map, center, 12f, "warmup", 6)) { yield return w; }
            }
            WorldObject_Submarine sub = null;
            yield return new WaitUntil(() => (sub = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>().FirstOrDefault(s => s.HasMap)) != null, 3000);
            ctx.Require(sub != null, "a submarine world object was created");
            // 出港時 DMSE 把船體移到航行地圖正中央（海洋層的 centerHullOnShipMap）：航行地圖上的座標都要加上這個位移
            IntVec3 shift = HullMoveHooks.LastOffsetTo(sub.Map);
            ctx.Check(shift != IntVec3.Zero, $"the hull was centred on the voyage map ({shift})");
            IntVec3 subCenter = center + shift;
            CellRect subDeck = deck.MovedBy(shift);
            if (visuals)
            {
                yield return new WaitRealtime(() => Find.WindowStack.IsOpen<TransferTakeoffCutscene>(), 5f);
                ctx.Note($"takeoff cutscene open: {Find.WindowStack.IsOpen<TransferTakeoffCutscene>()} (Odyssey {ModsConfig.OdysseyActive})");
                ctx.Check(!ModsConfig.OdysseyActive || Find.WindowStack.IsOpen<SubmarineTakeoffCutscene>(), "the cast-off cutscene is the submarine's");
                // 2026-11-15：過場暫停遊戲，原地空出來的船形要立刻是開放水域（外海層才會畫），不能等下一次水位更新
                MapComponent_Flooding originFlooding = map.Flooding();
                if (originFlooding != null && originFlooding.Active)
                {
                    int seaCells = deck.Cells.Count(c => c.GetRoom(map) is Room r && originFlooding.IsOpenWater(r));
                    int drawn = deck.Cells.Count(c => c.GetRoom(map) is Room r && originFlooding.IsOpenWater(r) && originFlooding.IsOpenAt(c));
                    ctx.Check(seaCells > 0 && drawn == seaCells, $"the vacated hull is open water during the cutscene ({drawn}/{seaCells} cells)");
                }
                float cutsceneStart = Time.realtimeSinceStartup;
                foreach (float at in new[] { 1.2f, 4f, 7f, 9f })
                {
                    yield return new WaitRealtime(() => Time.realtimeSinceStartup - cutsceneStart >= at, 12f);
                    if (!Find.WindowStack.IsOpen<TransferTakeoffCutscene>()) { break; }
                    ctx.Screenshot($"takeoff_{at:0.#}s");
                    yield return new WaitFrames(2);
                }
                yield return new WaitRealtime(() => !Find.WindowStack.IsOpen<TransferTakeoffCutscene>(), 15f);
                Prefs.GravshipCutscenes = cutscenesBefore;
                AcceptanceDriver.KeepWindowOpen = null;
                yield return new WaitFrames(10);
                Current.Game.CurrentMap = sub.Map;
                foreach (Wait w in ctx.ScreenshotOn(sub.Map, subCenter, 12f, "under_way_close", 30)) { yield return w; }
                foreach (Wait w in ctx.ScreenshotOn(sub.Map, subCenter, 24f, "under_way_wide", 30)) { yield return w; }

                // 艙外淡出到界外色、鏡頭持續搖晃
                Map voyageMap = sub.Map;
                ctx.Check(VoyageView.IsVoyageMap(voyageMap), "the submarine map counts as under way");
                ctx.Check(DefDatabase<ShaderTypeDef>.GetNamedSilentFail("DepthRim_VoyageDarkness")?.Shader != null, "the voyage darkness shader resolves");
                IntVec3 far = new IntVec3(2, 0, 2);
                SectionLayer farLayer = voyageMap.mapDrawer.SectionAt(far).GetLayer(typeof(SectionLayer_VoyageDarkness));
                int farVerts = farLayer?.subMeshes.Where(s => s.verts != null).Sum(s => s.verts.Count) ?? 0;
                ctx.Check(farVerts > 0, $"the open sea far from the hull is covered ({farVerts} verts)");
                SectionLayer hullLayer = voyageMap.mapDrawer.SectionAt(subCenter).GetLayer(typeof(SectionLayer_VoyageDarkness));
                ctx.Check(hullLayer != null, "the hull's section has the darkness layer");
                ctx.Check(SectionLayer_VoyageDarkness.IsHull(subCenter, voyageMap), "the deck centre is hull (not covered)");
                ctx.Check(SectionLayer_VoyageDarkness.FieldFor(voyageMap)[far.z * voyageMap.Size.x + far.x] >= VoyageView.FadeCells, "the map corner is fully faded");
                Vector2 o1 = Patch_SeaCameraSway.lastVoyageOffset;
                yield return new WaitRealtime(() => false, 1.5f);
                Vector2 o2 = Patch_SeaCameraSway.lastVoyageOffset;
                ctx.Check(o1 != Vector2.zero && o2 != o1 && o2.magnitude < 0.1f, $"the camera sways gently under way ({o1} → {o2})");

                // 巡航：微粒往航向反方向流過
                yield return new WaitTicks(240);
                Vector2 heading = VoyageMotion.Heading, flowNow = VoyageMotion.Flow;
                ctx.Note($"heading {heading}, flow {flowNow}, thrust {sub.ThrustFraction:P0}");
                ctx.Check(heading.sqrMagnitude > 0.5f, "the voyage knows its heading from the thrusters");
                ctx.Check(Vector2.Dot(flowNow, heading) < -0.5f, $"cruising, the specks stream past against the heading ({flowNow})");
                ctx.Check(VoyageMotion.DiveMix < 0.05f, "not changing depth while cruising");
                Vector2 off1 = VoyageMotion.Offset;
                yield return new WaitTicks(60);
                ctx.Check((VoyageMotion.Offset - off1).magnitude > 0.3f, "the specks keep moving");
                foreach (Wait w in ctx.ScreenshotOn(voyageMap, subCenter, 24f, "cruising", 30)) { yield return w; }

                // 下潛／上浮：放射狀擴散或收攏，鏡頭改成前後俯仰（只改畫面狀態，不真的換層）
                DepthTier savedTarget = sub.targetTier;
                int savedDiveTick = sub.nextDiveTick;
                foreach (int dir in new[] { 1, -1 })
                {
                    DepthTier other = dir > 0 ? (sub.cruiseTier < DepthTier.Seabed ? sub.cruiseTier + 1 : sub.cruiseTier - 1)
                                              : (sub.cruiseTier > DepthTier.Shallow ? sub.cruiseTier - 1 : sub.cruiseTier + 1);
                    int expect = other > sub.cruiseTier ? 1 : -1;
                    sub.targetTier = other;
                    sub.nextDiveTick = Find.TickManager.TicksGame + 999999;
                    yield return new WaitTicks(360);
                    ctx.Check(VoyageMotion.DiveMix > 0.8f && Mathf.Sign(VoyageMotion.ZoomRate) == expect,
                        $"{(expect > 0 ? "diving" : "surfacing")}: the specks {(expect > 0 ? "spread out" : "draw in")} (mix {VoyageMotion.DiveMix:F2}, rate {VoyageMotion.ZoomRate:F3})");
                    foreach (Wait w in ctx.ScreenshotOn(voyageMap, subCenter, 24f, expect > 0 ? "diving" : "surfacing", 30)) { yield return w; }
                }
                sub.targetTier = savedTarget;
                sub.nextDiveTick = savedDiveTick;
                yield return new WaitTicks(360);
                ctx.Check(VoyageMotion.DiveMix < 0.2f, $"back to cruising, the dive motion fades out ({VoyageMotion.DiveMix:F2})");
                CameraJumper.TryJump(sub);
                yield return new WaitFrames(40);
                ctx.Screenshot("world_map_submarine");
                yield return new WaitFrames(20);
                CameraJumper.TryHideWorld();
                yield return new WaitFrames(5);
            }
            Map subMap = sub.Map;
            ctx.Check(sub.def.defName == "DepthRim_Submarine", "the ship world object is the DepthRim submarine");
            ctx.Check(sub.InFlight, $"the submarine is under way ({sub.phase})");
            // 潛艇用語（DMSE TransferText，2026-11-11）：信件、階段名稱與預設船名是航海用語，太空船照用 DMSE 的
            ctx.Check(Find.LetterStack.LettersListForReading.Any(l => l.Label == "DepthRim.Submarine.Letter.SeparatedLabel".Translate()), "the cast-off letter uses submarine wording");
            ctx.Check(sub.CurrentPhaseLabel == ("DepthRim.Submarine.Phase." + sub.phase).Translate(), $"the voyage stage uses submarine wording ({sub.CurrentPhaseLabel})");
            ctx.Check(TransferText.Key("DMSE.Transfer.Phase.Coast", sub) == "DepthRim.Submarine.Phase.Coast" && TransferText.Key("DMSE.Transfer.Phase.Coast", PlanetLayerDefOf.Orbit) == "DMSE.Transfer.Phase.Coast",
                "only the ocean layer swaps the transfer wording");
            ctx.Check(TransferText.Key("DMSE.Transfer.UI.Footprint", sub) == "DMSE.Transfer.UI.Footprint", "texts the submarine does not override fall back to DMSE's");
            ctx.Check(chargeBefore - batteryComp.StoredEnergy < 20f, $"casting off draws no voyage charge up front ({chargeBefore:0} -> {batteryComp.StoredEnergy:0} Wd)");
            ctx.Check(helm.Map == subMap && thruster.Map == subMap && keel.Map == subMap && battery.Map == subMap, "the hull buildings moved to the submarine map");
            ctx.Check(cargo.Map == subMap && cargo.Position == subCenter + new IntVec3(1, 0, -3), "cargo kept its position aboard");
            ctx.Check(subDeck.CenterCell.DistanceTo(subMap.Center) <= 1.5f, $"the hull sits in the middle of the submarine map (deck centre {subDeck.CenterCell}, map centre {subMap.Center}, shift {shift})");
            ctx.Check(subDeck.Cells.All(c => subMap.terrainGrid.TerrainAt(c) == platform), "the whole deck is on the submarine map");
            ctx.Check(subMap.AllCells.Where(c => !subDeck.Contains(c)).All(c => subMap.terrainGrid.TerrainAt(c) == abyss), "everything outside the hull is abyss");
            ctx.Check(deck.Cells.All(c => map.terrainGrid.TerrainAt(c) == abyss), "the shipyard is open abyss again");
            ctx.Check(!map.AllCells.Any(c => map.terrainGrid.TerrainAt(c) == TerrainDefOf.Space), "no space terrain on the shipyard map");
            if (dryRoom.Count > 0)
            {
                float shipyardWater = dryRoom.Max(c => subMap.Flooding().LevelAt(c + shift));
                ctx.Check(shipyardWater < 0.05f, $"the drained compartment is still dry after casting off ({shipyardWater:F2})");
                yield return Updates(2);
                float afterUpdates = dryRoom.Max(c => subMap.Flooding().LevelAt(c + shift));
                ctx.Check(afterUpdates < 0.05f, $"and stays dry at sea ({afterUpdates:F2})");
                ctx.Check(subMap.Flooding().LevelAt(subCenter + new IntVec3(-4, 0, -4)) > 0.9f, "while the open deck is flooded");
            }

            // 收掉潛艇地圖、還原船塢地形與附近的海洋格
            void RemoveSubmarine()
            {
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
            }

            if (rope)
            {
                foreach (Wait w in Tests_RopeAnchor.Voyage(ctx, sub, subCenter, dryRoom.Select(c => c + shift).ToList())) { yield return w; }
                RemoveSubmarine();
                yield return Updates(1);
                yield break;
            }

            if (overboard)
            {
                // ── 航行中落海：艙外的人失蹤，艙室裡的人沒事，不在航行時什麼都不會發生 ──
                yield return new WaitTicks(VoyageMath.ThrustCheckInterval + 10);
                ctx.Require(VoyageOverboard.Underway(sub), $"under way with thrust ({sub.phase}, thrust {sub.ThrustFraction:P0})");
                ctx.Require(dryRoom.Count > 0, "the shipyard has a sealed compartment");
                IntVec3 cabinCell = dryRoom[0] + shift;
                IntVec3 deckCell = subCenter + new IntVec3(-4, 0, -4);
                IntVec3 seaCell = subDeck.ExpandedBy(2).EdgeCells.FirstOrDefault(c => c.InBounds(subMap) && c.Walkable(subMap));
                ctx.Require(seaCell.IsValid && !subDeck.Contains(seaCell), $"an open-sea cell next to the hull ({seaCell})");

                // 不在航行：視為已抵達（DMSE 的 Arrived），等兩輪檢查
                TransferPhase savedPhase = sub.phase;
                sub.phase = TransferPhase.Arrived;
                Pawn inside = DepthRimFixture.SpawnColonist(subMap, cabinCell);
                Pawn onDeck = DepthRimFixture.SpawnColonist(subMap, deckCell);
                Pawn inSea = DepthRimFixture.SpawnColonist(subMap, seaCell);
                // 非我方的人（訪客派系的村民）一樣會被沖走
                Faction strangerFaction = Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => !f.IsPlayer && f.def.humanlikeFaction && !f.HostileTo(Faction.OfPlayer));
                ctx.Require(strangerFaction != null, "a non-player humanlike faction exists");
                Pawn stranger = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, strangerFaction, forceGenerateNewPawn: true, canGeneratePawnRelations: false));
                GenSpawn.Spawn(stranger, seaCell, subMap, WipeMode.Vanish);
                ctx.Check(!VoyageOverboard.IsOutside(inside), $"the sealed compartment counts as inside ({cabinCell})");
                ctx.Check(VoyageOverboard.IsOutside(onDeck), $"the unroofed deck counts as outside ({deckCell})");
                ctx.Check(VoyageOverboard.IsOutside(inSea), $"the open sea counts as outside ({seaCell})");
                int lettersBefore = Find.LetterStack.LettersListForReading.Count(l => l.Label == "DepthRim.Voyage.OverboardLabel".Translate());
                yield return new WaitTicks(VoyageOverboard.CheckInterval * 2 + 10);
                ctx.Check(!VoyageOverboard.Underway(sub), "not under way after arriving");
                ctx.Check(inside.Spawned && onDeck.Spawned && inSea.Spawned && stranger.Spawned && onDeck.Map == subMap && inSea.Map == subMap, "not under way: nobody outside goes missing");
                ctx.Check(VoyageOverboard.Sweep(sub).Count == 0, "a sweep while not under way loses nobody");

                // 回到航行
                sub.phase = savedPhase;
                yield return new WaitTicks(VoyageMath.ThrustCheckInterval + VoyageOverboard.CheckInterval + 20);
                ctx.Check(VoyageOverboard.Underway(sub), $"under way again ({sub.phase}, thrust {sub.ThrustFraction:P0})");
                ctx.Check(inside.Spawned && inside.Map == subMap && inside.Position == cabinCell, "the colonist in the sealed compartment stays aboard");
                foreach (Pawn p in new[] { onDeck, inSea, stranger })
                {
                    ctx.Check(!p.Spawned && !p.Dead && p.IsWorldPawn(), $"{p.LabelShort} outside the hull went missing (spawned {p.Spawned}, dead {p.Dead}, world pawn {p.IsWorldPawn()})");
                }
                int lettersAfter = Find.LetterStack.LettersListForReading.Count(l => l.Label == "DepthRim.Voyage.OverboardLabel".Translate());
                ctx.Check(lettersAfter == lettersBefore + 1, $"one lost-overboard letter ({lettersBefore} -> {lettersAfter})");
                Letter letter = Find.LetterStack.LettersListForReading.LastOrDefault(l => l.Label == "DepthRim.Voyage.OverboardLabel".Translate());
                string text = (letter as ChoiceLetter)?.Text.ToString() ?? "";
                ctx.Check(text.Contains(onDeck.LabelCap) && text.Contains(inSea.LabelCap) && text.Contains(stranger.LabelCap) && !text.Contains(inside.LabelCap), "the letter names the two lost colonists and the stranger only");

                foreach (Pawn p in new[] { onDeck, inSea, stranger })
                {
                    if (p.IsWorldPawn()) { Find.WorldPawns.RemoveAndDiscardPawnViaGC(p); }
                }
                RemoveSubmarine();
                yield return Updates(1);
                yield break;
            }

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
                ctx.Check(!TransferReadiness.Check(subConsole, null, includeWarnings: false).Any(i => i.IsError && i.text == TransferText.Key("DMSE.Transfer.Issue.InFlight", subConsole).Translate()), "a new voyage can be planned from where it stopped");

                RemoveSubmarine();
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

                // 玩家自己選下錨的鄰格：只有符合規則的鄰格可選
                List<PlanetTile> choices = sub.Anchorages();
                ctx.Note($"{choices.Count} anchorages to choose from");
                ctx.Check(choices.Contains(anchorage) && choices.All(t => sub.AnchorageBlockReason(t) == null), "every offered anchorage is valid");
                ctx.Check(sub.AnchorageBlockReason(sub.Tile) != null, "the submarine's own tile is not an anchorage");
                List<PlanetTile> around = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(sub.Tile, around);
                PlanetTile farTile = PlanetTile.Invalid;
                for (int i = 0; i < sub.Tile.Layer.TilesCount && !farTile.Valid; i++)
                {
                    PlanetTile t = new PlanetTile(i, sub.Tile.Layer);
                    if (t != sub.Tile && !around.Contains(t)) { farTile = t; }
                }
                ctx.Check(sub.AnchorageBlockReason(farTile) == "DepthRim.Anchor.Spot.NotAdjacent".Translate(), "a tile further away is rejected");
                // 下錨併入航行指令：選旁邊的海域時選單裡有「下錨」
                Command_Action navCmd = sub.GetGizmos().OfType<Command_Action>().FirstOrDefault(g => g.icon == SubmarineNavigation.NavigateIcon);
                ctx.Check(navCmd != null && !navCmd.Disabled, "the navigate command is offered");
                Thing subHelm = SubmarineNavigation.HelmOf(sub);
                ctx.Check(SubmarineNavigation.TileBlockReason(subHelm, anchorage, 0) == null, "a neighbouring anchorage can be picked");
                ctx.Check(SubmarineNavigation.OptionsFor(subHelm, anchorage).Any(o => o.Label == "DepthRim.Nav.Anchor".Translate() && !o.Disabled), "picking it offers an enabled anchor option");
                ctx.Check(SubmarineNavigation.OptionsFor(subHelm, sub.Tile).Any(o => o.Label.StartsWith("DepthRim.Sub.Cruise".Translate(DepthTier.Shallow.Label()))), "picking its own tile offers depth changes");
                // 2026-11-16 指令圖示：五張 Textures/UI 圖都載得到；選單項目依深度變化用上浮／下潛／巡航，下錨用下錨圖
                ctx.Check(new[] { SubmarineIcons.RoutePlanning, SubmarineIcons.Anchoring, SubmarineIcons.Ascent, SubmarineIcons.Descent, SubmarineIcons.Cruising }.All(t => t != null && t != BaseContent.BadTex),
                    "the five submarine command icons load");
                ctx.Check(navCmd != null && navCmd.icon == SubmarineIcons.RoutePlanning, "the navigate command uses the route planning icon");
                {
                    System.Func<FloatMenuOption, Texture2D> iconOf = o => HarmonyLib.Traverse.Create(o).Field("iconTex").GetValue<Texture2D>();
                    ctx.Check(SubmarineNavigation.OptionsFor(subHelm, anchorage).Where(o => o.Label == "DepthRim.Nav.Anchor".Translate()).All(o => iconOf(o) == SubmarineIcons.Anchoring), "the anchor option uses the anchoring icon");
                    List<FloatMenuOption> own = SubmarineNavigation.OwnTileOptions(sub);
                    ctx.Check(own.Where(o => o.Label.StartsWith("DepthRim.Sub.Cruise".Translate(sub.cruiseTier.Label()))).All(o => iconOf(o) == SubmarineIcons.Cruising)
                              && own.Where(o => o.Label.StartsWith("DepthRim.Surface.Label".Translate())).All(o => iconOf(o) == SubmarineIcons.Ascent)
                              && own.All(o => iconOf(o) != null), "own-tile options: same depth cruising, surfacing ascent, every option has an icon");
                    ctx.Check(sub.StopGizmo().icon == SubmarineIcons.Anchoring, "stop here uses the anchoring icon");
                }
                navCmd?.action();
                ctx.Check(Find.WorldTargeter.IsTargeting, "the navigate command asks where on the world map");
                Find.WorldTargeter.StopTargeting();
                CameraJumper.TryHideWorld();
                yield return new WaitFrames(5);

                sub.AnchorAt(anchorage);
                MapParent site = Find.WorldObjects.MapParentAt(anchorage);
                ctx.Require(site != null && site.def == DepthRimDefOf.DepthRim_OceanSite, "anchoring created an ocean site next to the submarine");
                ctx.Check(sub.landingTarget == anchorage, "the submarine is landing at the anchorage");
                yield return new WaitUntil(() => site.HasMap, 6000);
                ctx.Require(site.HasMap, "the anchorage map was generated");
                Map seafloorMap = site.Map;
                // 地圖產生後由 DMSE 的降落選位讓玩家決定船體停在哪裡
                yield return new WaitRealtime(() => Find.DesignatorManager.SelectedDesignator is Designator_LandTransferShip, 10f);
                ctx.Check(Find.DesignatorManager.SelectedDesignator is Designator_LandTransferShip, "then the player places the hull on the seafloor map");
                Find.DesignatorManager.Deselect();
                yield return new WaitFrames(10);
                TransferLandingUtility.LandAutomatically(sub);
                yield return new WaitUntil(() => sub.Destroyed, 3000);
                ctx.Require(sub.Destroyed, "the submarine settled and its world object is gone");
                // 觸底揚塵：船體壓到海床的地方周圍揚起沉積物
                MapComponent_TouchdownDust dust = seafloorMap.GetComponent<MapComponent_TouchdownDust>();
                int dustAtLanding = dust.emitted;
                bool dustActive = dust.Active;
                ctx.Note($"touchdown dust: {dust.lastRingCells} ring cells, {dustAtLanding} puffs at touchdown, active={dustActive}");
                ctx.Check(helm.Map == seafloorMap && thruster.Map == seafloorMap && keel.Map == seafloorMap, "the hull is on the seafloor map");
                List<IntVec3> deckCells = seafloorMap.AllCells.Where(c => seafloorMap.terrainGrid.TerrainAt(c) == platform).ToList();
                ctx.Check(deckCells.Count == deck.Area, $"the whole deck landed ({deckCells.Count} cells)");
                IntVec3 landOffset = helm.Position - (center + new IntVec3(-2, 0, 3));
                yield return Updates(1);
                float anchoredWater = dryRoom.Max(c => seafloorMap.Flooding().LevelAt(c + landOffset));
                ctx.Check(anchoredWater < 0.05f, $"the drained compartment is still dry after anchoring ({anchoredWater:F2}, offset {landOffset})");
                MapComponent_SeabedMemory memory = seafloorMap.GetComponent<MapComponent_SeabedMemory>();
                int onSeabed = deckCells.Count(c => memory.TryGetUnder(c, out _));
                ctx.Note($"{onSeabed} of {deckCells.Count} deck cells rest on seabed");
                if (onSeabed > 0)
                {
                    ctx.Check(dust.lastRingCells > 0 && dustAtLanding > 0, $"touching down on the seabed kicks up a ring of dust ({dust.lastRingCells} cells, {dustAtLanding} puffs)");
                    ctx.Check(dust.emitted > dustAtLanding, $"the dust keeps billowing after touchdown ({dust.emitted} puffs)");
                    ctx.Check(!dust.Active, "and settles within a few seconds");
                }
                else
                {
                    ctx.Check(dust.emitted == 0, "no seabed under the hull: no touchdown dust");
                }
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
            IntVec3 edge = new IntVec3(subDeck.minX, 0, subDeck.CenterCell.z);
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
            foreach (Wait w in ctx.ScreenshotOn(subMap, subCenter, 9f, "submarine_under_way", 40)) { yield return w; }
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
