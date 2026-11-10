using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>魚雷：DMSE 飛彈換皮（設計稿 §11 M5）。</summary>
    public static class Tests_Torpedo
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>
        /// T1：冰洋測試地圖上蓋一間排乾的密閉艙室，牆屬於敵對陣營；海路 2～4 格外另開一個海洋地點，蓋電池與魚雷發射管、裝一枚魚雷。
        /// 檢查：魚雷不能打陸地；能打冰洋地點；發射後航跡是魚雷、記著發射地圖的深度；抵達後爆炸打壞艙壁，艙室開始進水。
        /// </summary>
        public static IEnumerable<Wait> T1_TorpedoStrike(TestContext ctx)
        {
            Map target = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            PlanetTile targetTile = target.Tile;

            // 測試用冰洋地點周圍多半是陸塊：先把附近三圈設成極地冰蓋，結束時還原（同 S2）
            Dictionary<PlanetTile, BiomeDef> biomeBefore = new Dictionary<PlanetTile, BiomeDef>();
            BiomeDef polar = DefDatabase<BiomeDef>.GetNamed("DepthRim_PolarIceCap");
            List<PlanetTile> frontier = new List<PlanetTile> { targetTile };
            for (int ring = 0; ring < 3; ring++)
            {
                List<PlanetTile> next = new List<PlanetTile>();
                foreach (PlanetTile t in frontier)
                {
                    List<PlanetTile> nb = new List<PlanetTile>();
                    Find.WorldGrid.GetTileNeighbors(t, nb);
                    foreach (PlanetTile n in nb)
                    {
                        if (n == targetTile || biomeBefore.ContainsKey(n)) { continue; }
                        biomeBefore[n] = n.Tile.PrimaryBiome;
                        n.Tile.PrimaryBiome = polar;
                        next.Add(n);
                    }
                }
                frontier = next;
            }
            OceanRouting.ClearCache();

            PlanetTile launchTile = PlanetTile.Invalid;
            foreach (PlanetTile t in biomeBefore.Keys)
            {
                int d = OceanRouting.Distance(targetTile, t);
                if (d >= 2 && d <= 3 && !Find.WorldObjects.AnyWorldObjectAt(t)) { launchTile = t; break; }
            }
            ctx.Require(launchTile.Valid, "found a free ocean tile two or three cells away by sea");
            ctx.Note($"launch site {launchTile}, target {targetTile}, {OceanRouting.Distance(launchTile, targetTile)} tiles by sea");

            MapParent site = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
            site.Tile = launchTile;
            site.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(site);
            Map launchMap = GetOrGenerateMapUtility.GetOrGenerateMap(launchTile, new IntVec3(100, 1, 100), DepthRimDefOf.DepthRim_OceanSite);
            ctx.Require(launchMap != null, "the launch site map was generated");

            // 發射場：電線、電池、發射管
            CellRect pad = CellRect.Empty;
            foreach (IntVec3 c in launchMap.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 5, 7);
                if (!r.ExpandedBy(3).InBounds(launchMap)) { continue; }
                if (r.ExpandedBy(1).Cells.All(n => n.GetTerrain(launchMap).affordances.Contains(TerrainAffordanceDefOf.Medium) && n.GetEdifice(launchMap) == null && n.GetFirstItem(launchMap) == null && n.GetFirstPawn(launchMap) == null))
                {
                    pad = r;
                    break;
                }
            }
            ctx.Require(pad.Area > 0, "found open seabed for the launch pad");
            foreach (IntVec3 c in pad.ExpandedBy(3).ClipInsideMap(launchMap)) { launchMap.fogGrid.Unfog(c); }
            foreach (IntVec3 c in pad)
            {
                Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                conduit.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(conduit, c, launchMap);
            }
            Thing Build(string def, IntVec3 at)
            {
                Thing t = ThingMaker.MakeThing(ThingDef.Named(def), ThingDef.Named(def).MadeFromStuff ? ThingDefOf.Steel : null);
                t.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(t, at, launchMap, Rot4.North);
                return t;
            }
            Thing battery = Build("Battery", pad.Min + new IntVec3(0, 0, 0));
            Thing tube = Build("DepthRim_TorpedoTube", pad.Min + new IntVec3(3, 0, 3));
            battery.TryGetComp<CompPowerBattery>().AddEnergy(10000f);
            CompPowerTrader tubePower = tube.TryGetComp<CompPowerTrader>();
            yield return new WaitUntil(() => tubePower.PowerOn, 1200);
            ctx.Require(tubePower.PowerOn, "the torpedo tube is powered");

            Thing torpedo = ThingMaker.MakeThing(ThingDef.Named("DepthRim_Torpedo"));
            ctx.Require(((IThingHolder)tube).GetDirectlyHeldThings().TryAdd(torpedo), "a torpedo was loaded into the tube");
            CompMissileLauncher_Rail rail = tube.TryGetComp<CompMissileLauncher_Rail>();
            ctx.Require(rail != null && rail.HasAmmo, "the tube has a torpedo ready");
            MissileConfig config = rail.BuildFiringConfig(torpedo);
            ctx.Require(TorpedoUtility.IsTorpedo(config), "the loaded round is a torpedo");
            ctx.Note($"torpedo range {config.Range} tiles, guidance {config.TargetingLabel}");

            // 只走海路
            PlanetTile land = PlanetTile.Invalid;
            for (int i = 0; i < launchTile.Layer.TilesCount; i++)
            {
                PlanetTile t = new PlanetTile(i, launchTile.Layer);
                if (!OceanRouting.Navigable(t)) { land = t; break; }
            }
            if (land.Valid)
            {
                ctx.Check(!MissileWorldUtility.CanEngage(config, launchTile, land, 0f, out string landReason) && landReason == "DepthRim.Torpedo.SeaOnly".Translate(config.Range).ToString(), "a torpedo cannot be fired at land");
            }
            ctx.Check(MissileWorldUtility.CanEngage(config, launchTile, targetTile, 0f, out string engageReason), $"the target is in torpedo range by sea ({engageReason})");
            PlanetTile surface = Find.WorldGrid.Surface.Tiles.Count > 0 ? new PlanetTile(0, Find.WorldGrid.Surface) : PlanetTile.Invalid;
            if (surface.Valid)
            {
                ctx.Check(!MissileWorldUtility.CanEngage(config, launchTile, surface, 0f, out _), "a torpedo cannot leave the ocean layer");
            }

            // 裝上被動歸向導引頭：找開著的敵方主動聲納
            CompMissileConfig configComp = torpedo.TryGetComp<CompMissileConfig>();
            configComp.EnsureInit();
            configComp.pending = configComp.config.Clone();
            configComp.pending.SetPart(MissilePartCategory.Guidance, DefDatabase<MissilePartDef>.GetNamed("DepthRim_Torpedo_Guidance_PassiveHoming"));
            configComp.ApplyAssemblyFree();
            config = rail.BuildFiringConfig(torpedo);
            ctx.Check(config.TargetingGuidance is GuidanceType_AntiRadiation, $"the torpedo now carries the passive homing seeker ({config.TargetingLabel})");

            // 目標：冰洋地圖上排乾的密閉艙室，牆屬於敵對陣營，裡面有一台敵方主動聲納（被動歸向導引頭的目標）
            Room room = Tests_Flooding.BuildCompartment(ctx, out CellRect inner, out List<Thing> walls);
            foreach (Wait w in Tests_Flooding.DrainCompartment(ctx, inner)) { yield return w; }
            Faction enemy = Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction);
            ctx.Require(enemy != null, "an enemy faction exists");
            foreach (Thing w in walls) { w.SetFaction(enemy); }
            Thing enemyBattery = DepthRimFixture.Spawn(ThingDef.Named("Battery"), inner.Min, target, null, enemy);
            enemyBattery.TryGetComp<CompPowerBattery>().AddEnergy(10000f);
            Thing enemySonar = DepthRimFixture.Spawn(ThingDef.Named("DepthRim_ActiveSonar"), inner.CenterCell, target, null, enemy);
            yield return new WaitUntil(() => enemySonar.TryGetComp<CompPowerTrader>().PowerOn, 1200);
            ctx.Check(enemySonar.TryGetComp<CompSearchRadar>().Active, "the enemy active sonar is pinging inside the compartment");
            int hpBefore = walls.Sum(w => w.HitPoints);
            ctx.Check(Tests_Flooding.Level(inner.CenterCell) <= 0.01f, "the target compartment is dry before the strike");

            // 發射
            ScorerProjectile_WorldObject track = rail.TryLaunchRound(new GlobalTargetInfo(targetTile), torpedo, false, out string failReason);
            ctx.Require(track != null, $"the torpedo was fired ({failReason})");
            ctx.Check(track.def.defName == "DepthRim_WorldObject_Torpedo", "the track is a torpedo world object");
            yield return new WaitUntil(() => Find.WorldObjects.Contains(track) || track.Destroyed, 2000);
            ctx.Require(Find.WorldObjects.Contains(track), "the torpedo is running through the water");
            MissileBehavior_Torpedo behavior = TorpedoUtility.BehaviorOf(track);
            ctx.Require(behavior != null, "the torpedo track carries its depth");
            ctx.Check(behavior.depth == FloodingUtility.DepthTierOf(launchMap), $"the torpedo runs at the launch map's depth ({behavior.depth})");
            int launchedAt = Find.TickManager.TicksGame;
            yield return new WaitUntil(() => track.Destroyed, 9000);
            ctx.Require(track.Destroyed, "the torpedo arrived");
            ctx.Note($"arrived after {Find.TickManager.TicksGame - launchedAt} ticks");

            // 落點爆炸打壞艙壁 → 進水
            List<string> seen = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                foreach (Thing t in target.listerThings.AllThings.Where(t => t is Projectile || t is Skyfaller))
                {
                    string s = $"{t.def.defName}@{t.Position}";
                    if (!seen.Contains(s)) { seen.Add(s); }
                }
                if (walls.Any(w => w.Destroyed) || walls.Sum(w => w.Destroyed ? 0 : w.HitPoints) < hpBefore) { break; }
                yield return new WaitTicks(60);
            }
            ctx.Note($"incoming on the target map: {string.Join(", ", seen.Take(12))}; compartment at {inner}, sonar at {enemySonar.Position}");
            yield return new WaitUntil(() => walls.Any(w => w.Destroyed) || walls.Sum(w => w.Destroyed ? 0 : w.HitPoints) < hpBefore, 2400);
            int destroyed = walls.Count(w => w.Destroyed);
            int hpAfter = walls.Sum(w => w.Destroyed ? 0 : w.HitPoints);
            ctx.Note($"walls: {destroyed} destroyed, hit points {hpBefore} -> {hpAfter}");
            ctx.Check(destroyed > 0 || hpAfter < hpBefore, "the blast damaged the compartment's hull");
            yield return Updates(3);
            float level = Tests_Flooding.Level(inner.CenterCell);
            bool open = DepthRimFixture.Flooding.IsOpenAt(inner.CenterCell);
            ctx.Note($"compartment level {level:P0}, inflow cells {DepthRimFixture.Flooding.InflowCells.Count}, open {open}");
            ctx.Check(level > 0.01f || open || DepthRimFixture.Flooding.InflowCells.Count > 0, "the damaged compartment takes on water");

            // 收尾
            Current.Game.CurrentMap = target;
            Current.Game.DeinitAndRemoveMap(launchMap, false);
            if (!site.Destroyed) { site.Destroy(); }
            foreach (KeyValuePair<PlanetTile, BiomeDef> kv in biomeBefore) { kv.Key.Tile.PrimaryBiome = kv.Value; }
            OceanRouting.ClearCache();
            yield return Updates(1);
        }
    }
}
