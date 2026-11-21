using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>潛水鐘與吊架 M5（Docs/海岸船舶與潛水鐘_機制設計.md §7）。</summary>
    public static class Tests_DivingBell
    {
        /// <summary>
        /// C5：在一格海洋層是淺海的海上建海面地點、鋪甲板，蓋吊架、潛水鐘（旁邊 2 格）、電池；鐘裡放一個穿潛水服的殖民者和鋼材。檢查：
        /// 港口（殖民地地圖）不能下鐘、吊架不能蓋在陸地上；下鐘：倒數只在有電時前進、期間船被封鎖；到底：鐘出現在正下方的海床（海洋地點）、
        /// 人與貨放出來；鐘裡的氧氣：有電時回充、沒電時先用殘氣再用潛水服；收鐘：鐘連人回到吊架旁、人放出來、封鎖解除；
        /// 放棄：鐘留在海床上失聯、吊架回到 Stowed、封鎖解除。最後還原。
        /// </summary>
        public static IEnumerable<Wait> C5_DivingBell(TestContext ctx)
        {
            Map home = DepthRimFixture.Home;
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            ctx.Require(ocean != null, "the ocean layer exists");
            // 找一格海洋層是淺海、上面是海的地方（兩層都沒有世界物件）
            PlanetTile below = PlanetTile.Invalid, above = PlanetTile.Invalid;
            for (int id = 0; id < ocean.TilesCount && !below.Valid; id += 3)
            {
                PlanetTile t = new PlanetTile(id, ocean);
                if (!OceanRouting.Navigable(t) || WorldObject_Submarine.SeafloorAt(t) != DepthTier.Shallow || Find.WorldObjects.AnyWorldObjectAt(t))
                {
                    continue;
                }
                PlanetTile up = OceanWorldView.TileAbove(t);
                if (!up.Valid || Find.WorldObjects.AnyWorldObjectAt(up) || OceanWorldView.TileBelow(up, ocean) != t)
                {
                    continue;
                }
                below = t;
                above = up;
            }
            ctx.Require(below.Valid, "found a shallow ocean tile with free sea above");
            ctx.Note($"surface {above}, seabed {below}");

            // ── 港口與碼頭 ──
            ThingDef davitDef = ThingDef.Named("DepthRim_BellDavit");
            ThingDef bellDef = ThingDef.Named("DepthRim_DivingBell");
            PlanetTile homeBelow = Building_BellDavit.SeabedTile(home, out string portReason);
            ctx.Check(!homeBelow.Valid && portReason == "DepthRim.Bell.InPort".Translate().ToString(), $"no bell in port ({portReason})");
            IntVec3 landCell = home.AllCells.First(c => c.Standable(home) && !c.Fogged(home) && c.GetEdifice(home) == null && !home.terrainGrid.TerrainAt(c).IsWater);
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(davitDef, landCell, Rot4.North, home).Accepted, "a davit cannot be built on land (a pier)");

            Map viewing = Find.CurrentMap;
            List<Thing> spawned = new List<Thing>();
            Pawn diver = null;
            MapParent seaSite = null;
            OverboardHazard.debugSeaFactor = 1f;
            try
            {
                Map sea = DebugActions_SeaSurface.CreateAt(above);
                ctx.Require(sea != null, "a sea-surface site above the shallows");
                seaSite = sea.Parent;
                Current.Game.CurrentMap = sea;
                TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
                CellRect deck = CellRect.CenteredOn(sea.Center, 4);
                foreach (IntVec3 c in deck) { sea.terrainGrid.SetTerrain(c, platform); }
                foreach (IntVec3 c in deck)
                {
                    Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                    conduit.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(conduit, c, sea);
                    spawned.Add(conduit);
                }
                Thing Build(ThingDef def, IntVec3 at)
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                    t.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(t, at, sea, Rot4.North);
                    spawned.Add(t);
                    return t;
                }
                IntVec3 center = deck.CenterCell;
                ctx.Check(GenConstruct.CanPlaceBlueprintAt(davitDef, center, Rot4.North, sea).Accepted, "a davit can be built on the deck");
                Building_BellDavit davit = (Building_BellDavit)Build(davitDef, center);
                Building_DivingBell bell = (Building_DivingBell)Build(bellDef, center + new IntVec3(2, 0, 0));
                Thing battery = Build(ThingDef.Named("Battery"), center + new IntVec3(-3, 0, -3));
                battery.TryGetComp<CompPowerBattery>().AddEnergy(5000f);
                CompPowerTrader power = davit.GetComp<CompPowerTrader>();
                yield return new WaitUntil(() => power.PowerOn, 1200);
                ctx.Require(power.PowerOn, "the davit is powered");

                diver = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                Apparel suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
                diver.apparel.Wear(suit, dropReplacedApparel: false);
                Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                steel.stackCount = 12;
                ctx.Require(bell.HeldThings.TryAdd(diver) && bell.HeldThings.TryAdd(steel), "a diver and some steel are in the bell");
                ctx.Check(davit.FindNearbyBell() == bell, "the davit finds the bell beside it");
                ctx.Check(davit.DeployBlockReason() == null, $"the bell can be lowered ({davit.DeployBlockReason()})");
                ctx.Check(DiveBellLock.Reason(sea) == null, "with the bell stowed nothing is locked");

                // ── 下鐘：倒數只在有電時前進 ──
                davit.StartDeploy();
                // 2026-11-22：收放一開始鐘就離開地圖（播下沉的離場動畫），倒數結束才出現在海床
                ctx.Check(davit.State == BellState.Deploying && !bell.Spawned && davit.InTransit && bell.InTransit, "lowering: the bell has left the deck and hangs in the davit");
                ctx.Check(BellFx.Playing(sea) > 0, "and the sinking animation plays on deck");
                ctx.Check(bell.PeopleAboard == 1 && bell.HeldThings.Contains(steel), "the diver and the cargo are still in the bell on the way down");
                ctx.Check(DiveBellLock.Reason(sea) == "DepthRim.Bell.NotStowed".Translate().ToString(), "while the bell is out the ship is locked");
                ctx.Check(davit.TransitTicks == BellMath.TransitTicks(DepthTier.Shallow), $"the shallows take {davit.TransitTicks} ticks");
                CompFlickable flick = davit.GetComp<CompFlickable>();
                flick.SwitchIsOn = false;
                yield return new WaitUntil(() => !power.PowerOn, 300);
                int before = davit.Progress;
                davit.AdvanceTicks(davit.TransitTicks);
                ctx.Check(davit.Progress == before && davit.State == BellState.Deploying, "without power the winch does not turn");
                float reserveBefore = bell.reserve;
                bell.UpdateAir(250);
                ctx.Check(bell.reserve < reserveBefore, "without power the people in the bell use its reserve air");
                flick.SwitchIsOn = true;
                yield return new WaitUntil(() => power.PowerOn, 600);
                davit.AdvanceTicks(davit.TransitTicks);
                ctx.Require(davit.State == BellState.Deployed, $"with power the bell reaches the bottom ({davit.State})");
                Map seabed = bell.Map;
                ctx.Check(seabed != null && seabed != sea && seabed.Tile == below && seabed.Parent is WorldObject_OceanSite, $"the bell is on the seabed straight below ({seabed?.Tile})");
                ctx.Check(diver.Spawned && diver.Map == seabed && diver.Position.InHorDistOf(bell.Position, 4f), "the diver stepped out beside the bell");
                ctx.Check(steel.Spawned && steel.Map == seabed, "the cargo came out too");
                ctx.Check(bell.PeopleAboard == 0, "the bell is empty after landing");
                ctx.Check(DiveBellLock.Reason(sea) != null, "the ship stays locked while the bell is down");
                foreach (Wait w in ctx.ScreenshotOn(seabed, bell.Position, 12f, "bell_on_seabed")) { yield return w; }
                yield return new WaitFrames(30);

                // ── 鐘裡的氧氣 ──
                diver.DeSpawn();
                ctx.Require(bell.HeldThings.TryAdd(diver), "the diver climbs back in");
                CompDivingAir air = CompDivingAir.WornBy(diver);
                air.TryConsume((int)(air.Props.capacityTicks * 0.5f));
                float half = air.AirPercent;
                bell.UpdateAir(250);
                ctx.Check(air.AirPercent > half, $"with power the suit refills inside the bell ({half:P0} -> {air.AirPercent:P0})");
                flick.SwitchIsOn = false;
                yield return new WaitUntil(() => !power.PowerOn, 300);
                bell.reserve = 0f;
                float suitBefore = air.AirPercent;
                bell.UpdateAir(250);
                ctx.Check(air.AirPercent < suitBefore, "with no power and no reserve the diver breathes from the suit");
                ctx.Check(davit.RetractBlockReason() == "DepthRim.Bell.NoPower".Translate().ToString(), "and the bell cannot be raised");
                flick.SwitchIsOn = true;
                yield return new WaitUntil(() => power.PowerOn, 600);

                // ── 收鐘 ──
                davit.StartRetract();
                ctx.Check(davit.State == BellState.Retracting && !bell.Spawned && davit.InTransit, "raising: the bell has left the seabed at once");
                ctx.Check(BellFx.Playing(seabed) > 0, "and the pull-up animation plays on the seabed");
                davit.AdvanceTicks(davit.TransitTicks);
                ctx.Check(davit.State == BellState.Stowed && bell.Spawned && bell.Map == sea && bell.OccupiedRect().ClosestCellTo(davit.Position).DistanceTo(davit.Position) <= Building_BellDavit.BellRange,
                    "the bell is back beside the davit");
                ctx.Check(diver.Spawned && diver.Map == sea, "the diver came up and stepped out on deck");
                ctx.Check(DiveBellLock.Reason(sea) == null, "the ship is free again");
                if (steel.Spawned && steel.Map == seabed) { steel.Destroy(); }

                // ── 下到一半放棄：鐘回到甲板 ──
                yield return new WaitTicks(5);
                davit.StartDeploy();
                ctx.Require(davit.InTransit, "lowering again");
                davit.AdvanceTicks(davit.TransitTicks / 2);
                davit.Abandon();
                ctx.Check(davit.State == BellState.Stowed && bell.Spawned && bell.Map == sea && bell.davit == null, "abandoning halfway down puts the bell back on deck");
                ctx.Check(bell.OccupiedRect().ClosestCellTo(davit.Position).DistanceTo(davit.Position) <= Building_BellDavit.BellRange + 2f, "next to the davit");

                // ── 放棄 ──
                yield return new WaitTicks(5);
                davit.StartDeploy();
                davit.AdvanceTicks(davit.TransitTicks);
                ctx.Require(davit.State == BellState.Deployed, "lowered again (empty)");
                Map seabed2 = bell.Map;
                davit.Abandon();
                ctx.Check(davit.State == BellState.Stowed && davit.Bell == null && bell.davit == null, "abandoning cuts the bell loose");
                ctx.Check(bell.Spawned && bell.Map == seabed2, "the bell stays where it is, on the seabed");
                ctx.Check(!bell.Supplied, "nobody supplies it air any more");
                ctx.Check(DiveBellLock.Reason(sea) == null, "the ship is free to go");
                ctx.Check(Find.LetterStack.LettersListForReading.Any(l => l.Label == "DepthRim.Bell.LostLabel".Translate()), "a letter reports the lost bell");
            }
            finally
            {
                OverboardHazard.debugSeaFactor = null;
                Current.Game.CurrentMap = viewing;
                if (diver != null && !diver.Destroyed) { if (diver.Spawned) { diver.DeSpawn(); } diver.Destroy(); }
                foreach (Thing t in spawned.Where(t => !t.Destroyed).ToList()) { t.Destroy(); }
                foreach (MapParent p in Find.WorldObjects.AllWorldObjects.OfType<MapParent>().Where(p => p.Tile == below || p.Tile == above).ToList())
                {
                    if (p.HasMap) { Current.Game.DeinitAndRemoveMap(p.Map, false); }
                    if (!p.Destroyed) { p.Destroy(); }
                }
            }
            yield return new WaitFrames(2);
        }

        /// <summary>
        /// C9（2026-11-22）：潛水鐘能直接下到深海平原（深海層級的海床）。在一格深海平原上方的海面鋪甲板、蓋吊架與鐘，
        /// 能下鐘、收放時間比淺海久，到底後鐘在正下方的海床地圖上。
        /// </summary>
        public static IEnumerable<Wait> C9_BellToSeabedPlain(TestContext ctx)
        {
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            ctx.Require(ocean != null, "the ocean layer exists");
            BiomeDef plain = DefDatabase<BiomeDef>.GetNamed("DepthRim_SeabedPlain");
            PlanetTile below = PlanetTile.Invalid, above = PlanetTile.Invalid;
            for (int id = 0; id < ocean.TilesCount && !below.Valid; id += 3)
            {
                PlanetTile t = new PlanetTile(id, ocean);
                if (t.Tile.PrimaryBiome != plain || !OceanRouting.Navigable(t) || Find.WorldObjects.AnyWorldObjectAt(t))
                {
                    continue;
                }
                PlanetTile up = OceanWorldView.TileAbove(t);
                if (!up.Valid || Find.WorldObjects.AnyWorldObjectAt(up) || OceanWorldView.TileBelow(up, ocean) != t)
                {
                    continue;
                }
                below = t;
                above = up;
            }
            ctx.Require(below.Valid, "found a seabed plain with free sea above");
            ctx.Check(WorldObject_Submarine.SeafloorAt(below) == DepthTier.Deep, $"the seabed plain lies at deep-sea depth ({WorldObject_Submarine.SeafloorAt(below)})");
            Map viewing = Find.CurrentMap;
            List<Thing> spawned = new List<Thing>();
            try
            {
                Map sea = DebugActions_SeaSurface.CreateAt(above);
                ctx.Require(sea != null, "a sea-surface site above the seabed plain");
                Current.Game.CurrentMap = sea;
                TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
                CellRect deck = CellRect.CenteredOn(sea.Center, 4);
                foreach (IntVec3 c in deck) { sea.terrainGrid.SetTerrain(c, platform); }
                foreach (IntVec3 c in deck)
                {
                    Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                    conduit.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(conduit, c, sea);
                    spawned.Add(conduit);
                }
                Thing Build(ThingDef def, IntVec3 at)
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                    t.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(t, at, sea, Rot4.North);
                    spawned.Add(t);
                    return t;
                }
                IntVec3 center = deck.CenterCell;
                Building_BellDavit davit = (Building_BellDavit)Build(ThingDef.Named("DepthRim_BellDavit"), center);
                Building_DivingBell bell = (Building_DivingBell)Build(ThingDef.Named("DepthRim_DivingBell"), center + new IntVec3(2, 0, 0));
                Thing battery = Build(ThingDef.Named("Battery"), center + new IntVec3(-3, 0, -3));
                battery.TryGetComp<CompPowerBattery>().AddEnergy(5000f);
                CompPowerTrader power = davit.GetComp<CompPowerTrader>();
                yield return new WaitUntil(() => power.PowerOn, 1200);
                ctx.Require(power.PowerOn, "the davit is powered");
                PlanetTile seabed = Building_BellDavit.SeabedTile(sea, out string why);
                ctx.Check(seabed == below, $"the bell can go down to the seabed plain ({why})");
                ctx.Check(davit.DeployBlockReason() == null, $"lowering is allowed ({davit.DeployBlockReason()})");
                davit.StartDeploy();
                ctx.Check(davit.TransitTicks == BellMath.TransitTicks(DepthTier.Deep) && davit.TransitTicks > BellMath.TransitTicks(DepthTier.Shallow),
                    $"the deep sea takes longer than the shallows ({davit.TransitTicks} ticks)");
                davit.AdvanceTicks(davit.TransitTicks);
                ctx.Require(davit.State == BellState.Deployed, $"the bell reaches the bottom ({davit.State})");
                Map floorMap = bell.Map;
                ctx.Check(floorMap != null && floorMap.Tile == below && floorMap.Parent is WorldObject_OceanSite, "the bell is on the seabed plain straight below");
                ctx.Check(bell.Position.Standable(floorMap) || bell.Position.GetTerrain(floorMap) != DepthRimDefOf.DepthRim_Abyss, "it rests on the sea floor, not over the abyss");
                foreach (Wait w in ctx.ScreenshotOn(floorMap, bell.Position, 12f, "bell_on_seabed_plain")) { yield return w; }
                yield return new WaitFrames(30);
                davit.StartRetract();
                davit.AdvanceTicks(davit.TransitTicks);
                ctx.Check(davit.State == BellState.Stowed && bell.Map == sea, "the bell comes back up");
            }
            finally
            {
                Current.Game.CurrentMap = viewing;
                foreach (Thing t in spawned.Where(t => !t.Destroyed).ToList()) { t.Destroy(); }
                foreach (MapParent p in Find.WorldObjects.AllWorldObjects.OfType<MapParent>().Where(p => p.Tile == below || p.Tile == above).ToList())
                {
                    if (p.HasMap) { Current.Game.DeinitAndRemoveMap(p.Map, false); }
                    if (!p.Destroyed) { p.Destroy(); }
                }
            }
            yield return new WaitFrames(2);
        }
    }
}
