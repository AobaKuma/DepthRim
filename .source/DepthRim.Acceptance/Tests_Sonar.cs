using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>聲納：DMSE 搜索雷達換皮（設計稿 §11 M4）。</summary>
    public static class Tests_Sonar
    {
        /// <summary>
        /// N1：在冰洋測試地圖上蓋電池、主動聲納與被動聲納。檢查：通電後地圖成為玩家的 BVR 節點；
        /// 偵測半徑取兩者中較大的（主動聲納）；只剩被動聲納時半徑變小、地圖不再算「有主動聲納」；
        /// 兩者都關掉後地圖不再是節點。
        /// </summary>
        public static IEnumerable<Wait> N1_Sonar(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return new WaitTicks(5);
            ThingDef activeDef = ThingDef.Named("DepthRim_ActiveSonar");
            ThingDef passiveDef = ThingDef.Named("DepthRim_PassiveSonar");

            // 找一塊 8×5、能蓋中型建築、沒有東西的海床
            CellRect site = CellRect.Empty;
            foreach (IntVec3 c in map.AllCells)
            {
                CellRect r = new CellRect(c.x, c.z, 8, 5);
                if (!r.ExpandedBy(1).InBounds(map)) { continue; }
                if (r.Cells.All(n => n.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Medium) && n.GetEdifice(map) == null && n.GetFirstItem(map) == null && n.GetFirstPawn(map) == null))
                {
                    site = r;
                    break;
                }
            }
            ctx.Require(site.Area > 0, "found open seabed for the sonar site");
            foreach (IntVec3 c in site.ExpandedBy(2))
            {
                if (c.InBounds(map)) { map.fogGrid.Unfog(c); }
            }
            List<Thing> built = new List<Thing>();
            foreach (IntVec3 c in site)
            {
                Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                conduit.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(conduit, c, map);
                built.Add(conduit);
            }
            Thing Build(ThingDef def, IntVec3 at)
            {
                Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                t.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(t, at, map, Rot4.North);
                built.Add(t);
                return t;
            }
            IntVec3 o = site.Min;
            Thing battery = Build(ThingDef.Named("Battery"), o + new IntVec3(0, 0, 3));
            Thing active = Build(activeDef, o + new IntVec3(3, 0, 2));
            Thing passive = Build(passiveDef, o + new IntVec3(6, 0, 2));
            battery.TryGetComp<CompPowerBattery>().AddEnergy(10000f);
            yield return new WaitTicks(5);
            battery.TryGetComp<CompPowerBattery>().AddEnergy(10000f);
            CompPowerTrader activePower = active.TryGetComp<CompPowerTrader>();
            CompPowerTrader passivePower = passive.TryGetComp<CompPowerTrader>();
            yield return new WaitUntil(() => activePower.PowerOn && passivePower.PowerOn, 1200);
            ctx.Require(activePower.PowerOn && passivePower.PowerOn, "both sonars are powered");

            CompSearchRadar activeRadar = active.TryGetComp<CompSearchRadar>();
            CompSearchRadar passiveRadar = passive.TryGetComp<CompSearchRadar>();
            ctx.Require(activeRadar != null && passiveRadar != null, "both sonars are DMSE search radars");
            ctx.Check(activeRadar.Active && passiveRadar.Active, "both sonars are operating");
            ctx.Check(SonarUtility.HasActiveSonar(map, Faction.OfPlayer), "the map has a working active sonar");

            BVRNode Node()
            {
                return WorldComponent_BVRNetwork.Current?.Nodes.FirstOrDefault(n => n.parent == map.Parent && n.faction == Faction.OfPlayer);
            }
            yield return new WaitUntil(() => Node() != null, 600);
            BVRNode node = Node();
            ctx.Require(node != null, "the ocean map became a player BVR node");
            float both = node.DetectionRange(BVRTargetProps.Default);
            float activeOnly = activeRadar.EffectiveWorldRange(BVRTargetProps.Default);
            float passiveOnly = passiveRadar.EffectiveWorldRange(BVRTargetProps.Default);
            ctx.Note($"detection range: node {both:0.##}, active {activeOnly:0.##}, passive {passiveOnly:0.##} tiles");
            ctx.Check(activeOnly > passiveOnly && passiveOnly > 0f, "active sonar hears farther than passive sonar");
            ctx.Check(Mathf.Abs(both - activeOnly) < 0.01f, "the node uses the longest-range sonar");

            // 只剩被動聲納
            CompFlickable activeFlick = active.TryGetComp<CompFlickable>();
            activeFlick.SwitchIsOn = false;
            yield return new WaitUntil(() => !activeRadar.Active, 300);
            ctx.Check(!SonarUtility.HasActiveSonar(map, Faction.OfPlayer), "with the active sonar off the map no longer pings");
            yield return new WaitTicks(130);
            node = Node();
            float passiveRange = node?.DetectionRange(BVRTargetProps.Default) ?? 0f;
            ctx.Check(node != null && Mathf.Abs(passiveRange - passiveOnly) < 0.01f, $"passive sonar alone still listens ({passiveRange:0.##} tiles)");

            // 兩者都關掉
            passive.TryGetComp<CompFlickable>().SwitchIsOn = false;
            yield return new WaitUntil(() => !passiveRadar.Active, 300);
            yield return new WaitTicks(130);
            node = Node();
            float none = node?.DetectionRange(BVRTargetProps.Default) ?? 0f;
            ctx.Check(none <= 0f, $"with both sonars off the map hears nothing ({none:0.##})");

            // 收尾
            foreach (Thing t in built)
            {
                if (!t.Destroyed) { t.Destroy(); }
            }
            yield return new WaitTicks(5);
        }
    }
}
