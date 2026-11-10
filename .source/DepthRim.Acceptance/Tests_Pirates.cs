using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace DepthRim.Acceptance
{
    /// <summary>敵方勢力（設計稿 §11 M7）。</summary>
    public static class Tests_Pirates
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        private static MissileWorldObject PlayerTorpedoAt(PlanetTile from, WorldObject target)
        {
            MissileConfig config = new MissileConfig(DefDatabase<MissileBodyDef>.GetNamed("DepthRim_TorpedoBody"));
            WorldObject_IncomingMissile wo = IncomingMissileUtility.Make(from, target.Tile, config, Faction.OfPlayer,
                worldObjectDef: DefDatabase<WorldObjectDef>.GetNamed("DepthRim_WorldObject_EnemyTorpedo"), target: target as MapParent);
            Find.WorldObjects.Add(wo);
            return wo;
        }

        /// <summary>
        /// X1：冰洋地圖附近放一艘海盜潛艇。檢查：成為 DMSE 的抽象防空節點；沒有聲納時是「未知聲納接觸」，開了主動聲納就分類出來；
        /// 巡弋不離開海洋；魚雷命中扣船殼、打到歸零沉沒；突擊艇追著移動的潛艇改航；生成的船體地圖有三個乾的密閉艙室、穿潛水服的船員、
        /// 戰利品，突擊艇的降落點會切入艙室；船員還在時移除地圖不會鑿沉、清空後會。另驗魚群與敵方魚雷艇。
        /// </summary>
        public static IEnumerable<Wait> X1_PirateSubmarine(TestContext ctx)
        {
            Map home = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            Faction enemy = Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && !f.def.hidden
                && f.def.humanlikeFaction && f.def.techLevel >= TechLevel.Industrial);
            ctx.Require(enemy != null, "an industrial enemy faction exists");
            WorldObjectDef subDef = DefDatabase<WorldObjectDef>.GetNamed("DepthRim_PirateSubmarine");
            Dictionary<PlanetTile, BiomeDef> biomeBefore = Tests_TorpedoDefense.OceanRings(home.Tile, 3);
            List<WorldObject> spawned = new List<WorldObject>();
            Map subMap = null;
            try
            {
                ctx.Require(EnemyTorpedoBoatUtility.TryFindTile(home.Tile, new IntRange(2, 2), out PlanetTile subTile), "found an ocean tile 2 tiles away by sea");
                WorldObject_PirateSubmarine sub = PirateSubmarineUtility.Spawn(subDef, subTile, enemy, 400f);
                spawned.Add(sub);
                ctx.Check(sub.hullPoints == sub.MaxHull && sub.MaxHull > 0, $"the pirate sub starts with a full hull ({sub.hullPoints})");
                ctx.Check(sub.GetComponent<WorldObjectComp_MissileBase>() != null, "the pirate sub fires torpedoes (DMSE missile base comp)");
                ctx.Check(TorpedoUtility.IsTorpedo(sub.GetComponent<WorldObjectComp_MissileBase>().BuildFireConfig()), "its rounds are torpedoes");
                WorldObjectComp_AirDefense defense = sub.GetComponent<WorldObjectComp_AirDefense>();
                ctx.Require(defense != null && defense.Available, "the pirate sub has an abstract anti-torpedo defence");
                yield return new WaitTicks(40);
                ctx.Check(WorldComponent_BVRNetwork.Current.Nodes.Any(n => n.parent == sub && n.defense != null), "the pirate sub is a BVR defence node without DMSE's radar network research");

                // 分類
                SonarContacts.debugAllClassified = false;
                bool sonarBefore = SonarUtility.HasActiveSonar(home, Faction.OfPlayer);
                if (!sonarBefore)
                {
                    ctx.Check(sub.Label == "DepthRim.Sonar.UnknownContact".Translate().ToString(), $"without sonar it is an unknown contact ({sub.Label})");
                }
                CellRect pad = CellRect.Empty;
                foreach (IntVec3 c in home.AllCells)
                {
                    CellRect r = new CellRect(c.x, c.z, 6, 4);
                    if (!r.ExpandedBy(3).InBounds(home)) { continue; }
                    if (r.ExpandedBy(1).Cells.All(n => n.GetTerrain(home).affordances.Contains(TerrainAffordanceDefOf.Medium)
                        && n.GetEdifice(home) == null && n.GetFirstItem(home) == null && n.GetFirstPawn(home) == null))
                    {
                        pad = r;
                        break;
                    }
                }
                ctx.Require(pad.Area > 0, "found seabed for a sonar");
                foreach (IntVec3 c in pad) { DepthRimFixture.Spawn(ThingDefOf.PowerConduit, c, home); }
                DepthRimFixture.Spawn(ThingDef.Named("Battery"), pad.Min, home).TryGetComp<CompPowerBattery>().AddEnergy(100000f);
                Thing sonar = DepthRimFixture.Spawn(ThingDef.Named("DepthRim_ActiveSonar"), pad.Min + new IntVec3(3, 0, 1), home);
                yield return new WaitUntil(() => sonar.TryGetComp<CompSearchRadar>().Active, 1200);
                yield return new WaitTicks(40);
                ctx.Check(SonarContacts.Classified(sub) && sub.Label == subDef.label, $"an active sonar close enough classifies it ({sub.Label})");

                // 巡弋
                for (int i = 0; i < 5; i++) { sub.Patrol(); }
                ctx.Check(OceanRouting.Navigable(sub.Tile) && OceanRouting.IsOcean(sub.Tile), $"patrolling keeps it at sea ({sub.Tile})");
                ctx.Check(sub.depth <= DepthTierUtility.MaxTierAt(sub.Tile), "it never runs below the seafloor");

                // 魚雷命中
                MissileWorldObject hit = PlayerTorpedoAt(home.Tile, sub);
                MissileBehavior_Torpedo behavior = TorpedoUtility.BehaviorOf(hit);
                ctx.Require(behavior != null, "a torpedo track toward the sub exists");
                int before = sub.hullPoints;
                ctx.Check(behavior.PreArrive(), "a torpedo reaching a mapless pirate sub is resolved by DepthRim");
                ctx.Check(sub.hullPoints < before && !sub.Destroyed, $"the hit wore the hull down ({before} -> {sub.hullPoints})");
                hit.Destroy();
                int hits = 1;
                while (!sub.Destroyed && hits < 20)
                {
                    sub.TakeTorpedoHit(hit.config.DamageAmount);
                    hits++;
                }
                ctx.Check(sub.Destroyed, $"enough hits sink it ({hits} hits of {hit.config.DamageAmount})");

                // 追擊
                ctx.Require(EnemyTorpedoBoatUtility.TryFindTile(home.Tile, new IntRange(2, 3), out PlanetTile sub2Tile), "found a tile for a second sub");
                WorldObject_PirateSubmarine sub2 = PirateSubmarineUtility.Spawn(subDef, sub2Tile, enemy, 400f);
                spawned.Add(sub2);
                Building_BoardingCraft craft = (Building_BoardingCraft)ThingMaker.MakeThing(BoardingDefOf.DepthRim_PlayerBoardingCraft);
                craft.SetFaction(Faction.OfPlayer);
                WorldObject_TravelingBoardingCraft chase = (WorldObject_TravelingBoardingCraft)WorldObjectMaker.MakeWorldObject(BoardingDefOf.DepthRim_TravelingBoardingCraft);
                chase.Setup(craft, home.Tile, sub2.Tile);
                Find.WorldObjects.Add(chase);
                spawned.Add(chase);
                ctx.Check(chase.targetParent == sub2, "the craft locks on to the pirate sub at its destination");
                PlanetTile oldTile = sub2.Tile;
                for (int i = 0; i < 10 && sub2.Tile == oldTile; i++) { sub2.Patrol(); }
                ctx.Require(sub2.Tile != oldTile, "the second sub moved");
                chase.arriveTick = Find.TickManager.TicksGame;
                yield return new WaitTicks(5);
                ctx.Check(!chase.Destroyed && chase.to == sub2.Tile, $"the craft re-routes after the moving sub ({chase.to} vs {sub2.Tile})");
                chase.Destroy();

                // 船體地圖
                subMap = GetOrGenerateMapUtility.GetOrGenerateMap(sub2.Tile, sub2.Ext.mapSize, sub2.def);
                ctx.Require(subMap != null && sub2.HasMap, "boarding generates the pirate sub's hull map");
                yield return Updates(2);
                CellRect hull = GenStep_PirateSubHull.HullRect(subMap, new IntVec2(23, 11));
                List<Room> rooms = hull.ContractedBy(1).Cells.Select(c => c.GetRoom(subMap)).Where(r => r != null && !r.IsDoorway).Distinct().ToList();
                MapComponent_Flooding flooding = subMap.GetComponent<MapComponent_Flooding>();
                ctx.Note($"hull {hull}, rooms {rooms.Count} ({string.Join(",", rooms.Select(r => r.CellCount))}), level {flooding?.LevelAt(hull.CenterCell):P0}");
                ctx.Check(rooms.Count == 3, "the hull has three compartments");
                ctx.Check(rooms.All(r => !r.TouchesMapEdge && r.OpenRoofCount == 0), "every compartment is sealed");
                ctx.Check(rooms.All(r => flooding.LevelOf(r) <= 0.01f), "the compartments start dry");
                List<Pawn> crew = subMap.mapPawns.AllPawnsSpawned.Where(p => p.Faction == enemy).ToList();
                ctx.Check(crew.Count > 0, $"the sub has a crew ({crew.Count})");
                ctx.Check(crew.Where(p => p.RaceProps.Humanlike).All(p => p.apparel.WornApparel.Any(a => a.def.defName == "DepthRim_DivingSuit")), "the crew wear diving suits");
                ctx.Check(crew.All(p => p.GetLord()?.LordJob is LordJob_DefendPoint), "the crew defend their boat");
                ctx.Check(subMap.listerThings.ThingsOfDef(ThingDefOf.Silver).Any(), "there is loot aboard");
                ctx.Check(BoardingUtility.TryFindLandingCell(subMap, enemy, out IntVec3 land, out bool inside) && inside, $"a boarding craft cuts into a compartment ({land})");
                PlanetTile frozen = sub2.Tile;
                sub2.Patrol();
                ctx.Check(sub2.Tile == frozen, "the sub holds still while boarded");
                ctx.Check(!sub2.ShouldRemoveMapNow(out _), "the map is kept while the boarding craft is still landing");
                sub2.mapGeneratedTick = Find.TickManager.TicksGame - WorldObject_PirateSubmarine.MapGraceTicks - 1;
                ctx.Check(sub2.ShouldRemoveMapNow(out bool scuttle) && !scuttle, "leaving with the crew alive does not sink it");
                foreach (Pawn p in crew) { if (!p.Dead) { p.Kill(null); } }
                ctx.Check(sub2.ShouldRemoveMapNow(out bool scuttle2) && scuttle2, "with the crew dead, leaving scuttles it");

                // 魚群
                WorldObject fish = WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("DepthRim_FishSchool"));
                ctx.Require(EnemyTorpedoBoatUtility.TryFindTile(home.Tile, new IntRange(3, 3), out PlanetTile fishTile), "found a tile for a fish school");
                fish.Tile = fishTile;
                Find.WorldObjects.Add(fish);
                spawned.Add(fish);
                ctx.Note($"fish school at {fishTile}, classified {SonarContacts.Classified(fish)}, label {fish.Label}");
                ctx.Check(fish.Label == "DepthRim.Sonar.UnknownContact".Translate().ToString() || fish.Label == fish.def.label, "a fish school is either an unknown contact or classified as fish");
                ((WorldObject_FishSchool)fish).expireTick = Find.TickManager.TicksGame;
                yield return new WaitTicks(5);
                ctx.Check(fish.Destroyed, "the fish school drifts off after a while");

                // 敵方魚雷艇一發就沉
                ctx.Require(EnemyTorpedoBoatUtility.TryFindTile(home.Tile, new IntRange(2, 3), out PlanetTile boatTile), "found a tile for an enemy boat");
                WorldObject_EnemyTorpedoBoat boat = EnemyTorpedoBoatUtility.Spawn(DefDatabase<WorldObjectDef>.GetNamed("DepthRim_EnemyTorpedoBoat"), boatTile, enemy);
                spawned.Add(boat);
                MissileWorldObject boatHit = PlayerTorpedoAt(home.Tile, boat);
                ctx.Check(TorpedoUtility.BehaviorOf(boatHit).PreArrive() && boat.Destroyed, "one torpedo sinks an enemy torpedo boat");
                boatHit.Destroy();
            }
            finally
            {
                SonarContacts.debugAllClassified = false;
                if (subMap != null && subMap.Parent != null && subMap.Parent.HasMap)
                {
                    Current.Game.CurrentMap = home;
                    Current.Game.DeinitAndRemoveMap(subMap, false);
                }
                foreach (WorldObject o in spawned) { if (!o.Destroyed) { o.Destroy(); } }
                foreach (KeyValuePair<PlanetTile, BiomeDef> kv in biomeBefore) { kv.Key.Tile.PrimaryBiome = kv.Value; }
                OceanRouting.ClearCache();
            }
            yield return Updates(1);
        }
    }
}
