using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>魚雷防禦與敵方魚雷艇（設計稿 §11 M5 5.7）。</summary>
    public static class Tests_TorpedoDefense
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>把 <paramref name="center"/> 周圍幾圈設成極地冰蓋（測試地點旁多半是陸塊），回傳原本的生態區以便還原。</summary>
        public static Dictionary<PlanetTile, BiomeDef> OceanRings(PlanetTile center, int rings)
        {
            Dictionary<PlanetTile, BiomeDef> before = new Dictionary<PlanetTile, BiomeDef>();
            BiomeDef polar = DefDatabase<BiomeDef>.GetNamed("DepthRim_PolarIceCap");
            List<PlanetTile> frontier = new List<PlanetTile> { center };
            for (int ring = 0; ring < rings; ring++)
            {
                List<PlanetTile> next = new List<PlanetTile>();
                foreach (PlanetTile t in frontier)
                {
                    List<PlanetTile> nb = new List<PlanetTile>();
                    Find.WorldGrid.GetTileNeighbors(t, nb);
                    foreach (PlanetTile n in nb)
                    {
                        if (n == center || before.ContainsKey(n)) { continue; }
                        before[n] = n.Tile.PrimaryBiome;
                        n.Tile.PrimaryBiome = polar;
                        next.Add(n);
                    }
                }
                frontier = next;
            }
            OceanRouting.ClearCache();
            return before;
        }

        private static List<MissileWorldObject> EnemyTorpedoes(Faction enemy)
            => Find.WorldObjects.AllWorldObjects.OfType<MissileWorldObject>()
                .Where(m => !m.Destroyed && m.Faction == enemy && TorpedoUtility.IsTorpedo(m.config)).ToList();

        /// <summary>
        /// T2：冰洋測試地圖是被攻擊的一方。海路 2～3 格外放一艘敵方魚雷艇（事件用的選址與生成），讓它的 DMSE 陣地元件開火。
        /// 第一輪：主動聲納＋火控聲納＋裝反魚雷魚雷的發射管 → 反魚雷魚雷被射出攔截。
        /// 第二輪：拿掉反魚雷魚雷，改蓋超空泡近防砲 → 近防砲在最後一段開火。
        /// 另外檢查分工：攻擊用魚雷不會被當攔截彈、反魚雷魚雷不打非魚雷、陸地層的節點不接戰魚雷。
        /// </summary>
        public static IEnumerable<Wait> T2_TorpedoDefense(TestContext ctx)
        {
            Map home = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            PlanetTile homeTile = home.Tile;
            Dictionary<PlanetTile, BiomeDef> biomeBefore = OceanRings(homeTile, 3);
            Faction enemy = Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction);
            ctx.Require(enemy != null, "an enemy faction exists");
            WorldObject_EnemyTorpedoBoat boat = null;
            try
            {
                // ---- 防禦陣地 ----
                CellRect pad = CellRect.Empty;
                foreach (IntVec3 c in home.AllCells)
                {
                    CellRect r = new CellRect(c.x, c.z, 13, 8);
                    if (!r.ExpandedBy(3).InBounds(home)) { continue; }
                    if (r.ExpandedBy(1).Cells.All(n => n.GetTerrain(home).affordances.Contains(TerrainAffordanceDefOf.Medium)
                        && n.GetEdifice(home) == null && n.GetFirstItem(home) == null && n.GetFirstPawn(home) == null))
                    {
                        pad = r;
                        break;
                    }
                }
                ctx.Require(pad.Area > 0, "found open seabed for the defence pad");
                foreach (IntVec3 c in pad.ExpandedBy(3).ClipInsideMap(home)) { home.fogGrid.Unfog(c); }
                foreach (IntVec3 c in pad) { DepthRimFixture.Spawn(ThingDefOf.PowerConduit, c, home); }
                Thing Build(string def, IntVec3 offset) => DepthRimFixture.Spawn(ThingDef.Named(def), pad.Min + offset, home);
                List<Thing> batteries = new List<Thing> { Build("Battery", new IntVec3(0, 0, 0)), Build("Battery", new IntVec3(0, 0, 3)) };
                foreach (Thing b in batteries) { b.TryGetComp<CompPowerBattery>().AddEnergy(100000f); }
                Thing sonar = Build("DepthRim_ActiveSonar", new IntVec3(3, 0, 1));
                Thing fcs = Build("DepthRim_FireControlSonar", new IntVec3(7, 0, 1));
                Thing tube = Build("DepthRim_TorpedoTube", new IntVec3(11, 0, 1));
                yield return new WaitUntil(() => sonar.TryGetComp<CompPowerTrader>().PowerOn && fcs.TryGetComp<CompPowerTrader>().PowerOn
                    && tube.TryGetComp<CompPowerTrader>().PowerOn, 1200);
                ctx.Require(sonar.TryGetComp<CompPowerTrader>().PowerOn && fcs.TryGetComp<CompPowerTrader>().PowerOn, "the sonars are powered");
                ThingOwner held = ((IThingHolder)tube).GetDirectlyHeldThings();
                ctx.Require(held.TryAdd(ThingMaker.MakeThing(ThingDef.Named("DepthRim_AntiTorpedo"))), "an anti-torpedo torpedo was loaded");
                Thing strikeRound = ThingMaker.MakeThing(ThingDef.Named("DepthRim_Torpedo"));
                ctx.Require(held.TryAdd(strikeRound), "an attack torpedo was loaded beside it");
                CompMissileLauncher_Interceptor launcher = tube.TryGetComp<CompMissileLauncher_Interceptor>();
                ctx.Require(launcher != null, "the tube is also an interceptor launcher");
                ctx.Check(TorpedoUtility.IsAntiTorpedo(launcher.GetLoadedMissileConfig()), "the interceptor round is the anti-torpedo torpedo, not the attack torpedo");

                // ---- 敵方魚雷艇 ----
                WorldObjectDef boatDef = DefDatabase<WorldObjectDef>.GetNamed("DepthRim_EnemyTorpedoBoat");
                ctx.Require(EnemyTorpedoBoatUtility.TryFindTile(homeTile, new IntRange(2, 3), out PlanetTile boatTile), "found a free ocean tile 2-3 tiles away by sea");
                boat = EnemyTorpedoBoatUtility.Spawn(boatDef, boatTile, enemy);
                ctx.Check(boat.depth <= DepthTierUtility.MaxTierAt(boatTile), $"the boat cruises no deeper than the seafloor ({boat.depth})");
                ctx.Check(EnemyTorpedoBoatUtility.PlayerOceanTargets().Contains(home.Parent), "the ocean test map is a torpedo target");
                WorldObjectComp_MissileBase missileBase = boat.GetComponent<WorldObjectComp_MissileBase>();
                ctx.Require(missileBase != null, "the boat carries DMSE's missile base comp");
                MissileConfig fire = missileBase.BuildFireConfig();
                ctx.Check(TorpedoUtility.IsTorpedo(fire), "the boat fires torpedoes");
                ctx.Check(fire.TargetingGuidance is GuidanceType_AntiRadiation || fire.TargetingGuidance is GuidanceType_AntiShip, $"the boat picks a seeker, not straight running ({fire.TargetingLabel})");
                ctx.Note($"boat at {boatTile}, {OceanRouting.Distance(homeTile, boatTile)} tiles by sea, depth {boat.depth}; home tier {FloodingUtility.DepthTierOf(home)}");

                // ---- 第一輪：反魚雷魚雷 ----
                int fired = missileBase.TryFireAtPlayerColonies();
                ctx.Require(fired > 0, $"the boat fired at the ocean map ({fired})");
                List<MissileWorldObject> salvo = EnemyTorpedoes(enemy);
                ctx.Require(salvo.Count > 0, "enemy torpedo tracks are running");
                ctx.Check(salvo.All(m => m.Destination == homeTile), "the torpedoes head for the ocean test map");
                MissileBehavior_Torpedo depth = TorpedoUtility.BehaviorOf(salvo[0]);
                ctx.Check(depth != null && depth.depth == boat.depth, $"the enemy torpedo runs at the boat's depth ({depth?.depth})");

                // 分工（不靠擲骰）：反魚雷魚雷能打這條魚雷；陸地層節點不接戰魚雷
                BVRNode homeNode = WorldComponent_BVRNetwork.Current.Nodes.FirstOrDefault(n => n.parent == home.Parent && n.faction == Faction.OfPlayer);
                ctx.Require(homeNode != null, "the ocean map is a player defence node");
                AirEngagement engagement = homeNode.EngagementFor(salvo[0], salvo[0], salvo[0].PositionAt(0), salvo[0].TicksToArrival, salvo[0].AltitudeAt(0));
                ctx.Check(launcher.CanEngage(engagement), "anti-torpedo torpedoes may engage an incoming torpedo");
                ctx.Check(homeNode.PolicyAllows(salvo[0]), "the ocean node engages the torpedo");
                // 領域分工（DMSE 的 CombatDomain）：魚雷是水下領域；火控聲納只導引水下，DMSE 的防空火控與 CIWS 只打空中
                ctx.Check(CombatDomainUtility.Of(salvo[0]) == CombatDomain.Underwater, "an enemy torpedo is an underwater-domain track");
                ctx.Check(homeNode.CoversDomain(CombatDomain.Underwater) && !homeNode.CoversDomain(CombatDomain.Air),
                    "the fire-control sonar guides underwater interceptors and never air ones");
                ctx.Check(!launcher.Engages(CombatDomain.Air), "anti-torpedo torpedoes are not fired at air targets");
                CompProperties_FireControlRadar airFc = ThingDef.Named("DMSE_EarlyFireControlRadar").GetCompProperties<CompProperties_FireControlRadar>();
                ctx.Check(airFc != null && !airFc.Covers(CombatDomain.Underwater), "DMSE's air fire-control radar does not guide at torpedoes");
                CompProperties_TerminalCIWS flak = ThingDef.Named("DMSE_Turret_LegacyFlak").GetCompProperties<CompProperties_TerminalCIWS>();
                ctx.Check(flak != null && !CombatDomainUtility.Covers(flak.domains, CombatDomain.Underwater), "DMSE's flak CIWS does not shoot at torpedoes");

                int heldBefore = held.Count;
                bool attempted = false;
                int start = Find.TickManager.TicksGame;
                yield return new WaitUntil(() =>
                {
                    foreach (MissileWorldObject m in salvo) { if (!m.Destroyed && m.State.interceptAttempts > 0) { attempted = true; } }
                    return salvo.All(m => m.Destroyed);
                }, 6000);
                ctx.Note($"round 1 over after {Find.TickManager.TicksGame - start} ticks; tube {heldBefore} -> {held.Count} rounds");
                ctx.Check(salvo.All(m => m.Destroyed), "every torpedo of the first salvo was resolved (intercepted or arrived)");
                ctx.Check(attempted || held.Count < heldBefore, "the fire-control sonar fired an anti-torpedo torpedo");
                ctx.Check(held.Contains(strikeRound), "the attack torpedo was not used as an interceptor");

                // ---- 第二輪：近防砲 ----
                foreach (Thing t in held.Where(t => t.def.defName == "DepthRim_AntiTorpedo").ToList()) { held.Remove(t); t.Destroy(); }
                if (sonar.Destroyed) { sonar = Build("DepthRim_ActiveSonar", new IntVec3(3, 0, 1)); }
                foreach (Thing b in batteries.Where(b => !b.Destroyed)) { b.TryGetComp<CompPowerBattery>().AddEnergy(100000f); }
                Thing gun = Build("DepthRim_SupercavitatingGun", new IntVec3(3, 0, 5));
                CompRefuelable ammo = gun.TryGetComp<CompRefuelable>();
                ammo.Refuel(60f);
                CompTorpedoPointDefense pd = gun.TryGetComp<CompTorpedoPointDefense>();
                yield return new WaitUntil(() => pd.Armed && sonar.TryGetComp<CompSearchRadar>().Active, 1200);
                ctx.Require(pd.Armed, "the point-defence gun is powered and loaded");
                ctx.Check(home.GetComponent<MapComponent_AirEffectors>().Effectors.Contains(pd), "the gun registered as a point-defence effector");

                fired = 0;
                for (int i = 0; i < 3 && fired == 0; i++) { fired = missileBase.TryFireAtPlayerColonies(); }
                salvo = EnemyTorpedoes(enemy);
                ctx.Require(salvo.Count > 0, "the boat fired a second salvo");
                engagement = homeNode.EngagementFor(salvo[0], salvo[0], salvo[0].PositionAt(0), salvo[0].TicksToArrival, salvo[0].AltitudeAt(0));
                ctx.Check(!pd.CanEngage(engagement) || salvo[0].TicksToArrival <= pd.Props.engageEtaTicks, "the gun holds fire while the torpedo is still far out");
                float fuelBefore = ammo.Fuel;
                start = Find.TickManager.TicksGame;
                yield return new WaitUntil(() => salvo.All(m => m.Destroyed), 6000);
                ctx.Note($"round 2 over after {Find.TickManager.TicksGame - start} ticks; darts {fuelBefore} -> {ammo.Fuel}, cooldown until {pd.cooldownUntil}");
                ctx.Check(salvo.All(m => m.Destroyed), "every torpedo of the second salvo was resolved");
                ctx.Check(ammo.Fuel < fuelBefore && pd.cooldownUntil > start, "the point-defence gun fired at the incoming torpedo");
                yield return Updates(2);
            }
            finally
            {
                if (boat != null && !boat.Destroyed) { boat.Destroy(); }
                foreach (MissileWorldObject m in EnemyTorpedoes(enemy)) { m.Destroy(); }
                foreach (KeyValuePair<PlanetTile, BiomeDef> kv in biomeBefore) { kv.Key.Tile.PrimaryBiome = kv.Value; }
                OceanRouting.ClearCache();
            }
        }
    }
}
