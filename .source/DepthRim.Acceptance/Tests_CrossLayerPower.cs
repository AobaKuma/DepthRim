using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>跨層電力 M5.5（Docs/海岸船舶與潛水鐘_機制設計.md §7.10）：支柱腳 → 支柱頂。</summary>
    public static class Tests_CrossLayerPower
    {
        /// <summary>
        /// H7：海面地圖上一艘浮船，旁邊一根支柱。水面層：浮船上的電線接支柱腳和一顆充好電的電池；平台層：甲板上的電線接支柱頂和一盞燈。
        /// 檢查：燈從水面層拿到電、支柱頂送的剛好是燈要的、支柱腳抽的多 5%（線損）；平台層加一顆空電池 → 送滿容量（2000 W）不再多；
        /// 拆掉水面層的電池 → 送端沒電、支柱頂不再發電；切換方向 → 平台層的電池送下水面層的燈；拆支柱 → 分配器裡沒有這條橋接。最後還原。
        /// </summary>
        public static IEnumerable<Wait> H7_CrossLayerPower(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            List<Thing> spawned = new List<Thing>();
            // 測試環境預設無限電力（電池永遠滿）；這裡要真的電網
            bool wasUnlimited = DebugSettings.unlimitedPower;
            DebugSettings.unlimitedPower = false;
            try
            {
                IntVec3 center = sea.Center;
                CellRect dock = new CellRect(center.x - 3, center.z, 7, 3);
                Tests_SeaSurface.LayHull(sea, dock);
                yield return new WaitFrames(3);
                IntVec3 p1 = new IntVec3(center.x, 0, dock.minZ - 5);
                Building_PillarFoot foot = Tests_SeaSurface.BuildPillar(sea, p1);
                yield return new WaitFrames(3);
                Map platform = SeaLevels.PlatformLevelOf(sea);
                Building_PlatformPillar top = foot.Top;
                ctx.Require(platform != null && top != null && top.Spawned, "the pillar created the platform level and its top");

                CompCrossLayerPower down = foot.GetComp<CompCrossLayerPower>();
                CompCrossLayerPower up = top.GetComp<CompCrossLayerPower>();
                ctx.Require(down != null && up != null, "both ends carry the cross-layer link");
                ctx.Check(down.IsSource && !up.IsSource && down.Partner == up && up.Partner == down, "the foot sends, the top receives, and they know each other");
                MapComponent_CrossLayerPower links = sea.GetComponent<MapComponent_CrossLayerPower>();
                ctx.Check(links != null && links.Sources.Contains(down), "the sea level's power links include the foot");
                MapComponent_CrossLayerPower upLinks = platform.GetComponent<MapComponent_CrossLayerPower>();
                ctx.Check(upLinks != null && upLinks.Sources.Contains(up) && !up.IsSource, "the platform level registers the top, which only receives by default");
                ctx.Check(foot.def.GetCompProperties<CompProperties_Power>().transmitsPower && top.def.GetCompProperties<CompProperties_Power>().transmitsPower, "both ends transmit power");

                Thing Build(ThingDef def, IntVec3 at, Map map)
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                    t.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(t, at, map, Rot4.North);
                    spawned.Add(t);
                    return t;
                }

                // 水面層：浮船上一排電線、一顆充好電的電池（送端不受限）
                for (int x = dock.minX; x <= dock.maxX; x++) { Build(ThingDefOf.PowerConduit, new IntVec3(x, 0, dock.minZ), sea); }
                Thing seaBattery = Build(ThingDef.Named("Battery"), new IntVec3(dock.maxX, 0, dock.minZ + 1), sea);
                seaBattery.TryGetComp<CompPowerBattery>().AddEnergy(seaBattery.TryGetComp<CompPowerBattery>().Props.storedEnergyMax);

                // 平台層：甲板（初始 5×5）上一格電線接支柱頂，旁邊一盞燈
                Build(ThingDefOf.PowerConduit, p1 + new IntVec3(2, 0, 0), platform);
                Thing lamp = Build(ThingDef.Named("StandingLamp"), p1 + new IntVec3(2, 0, 1), platform);
                CompPowerTrader lampPower = lamp.TryGetComp<CompPowerTrader>();
                float lampWatts = lampPower.Props.PowerConsumption;
                yield return new WaitTicks(5);
                ctx.Check(down.Trader.PowerNet != null && down.Trader.PowerNet == seaBattery.TryGetComp<CompPowerBattery>().PowerNet, "the foot is on the sea level's grid");
                ctx.Check(up.Trader.PowerNet != null && up.Trader.PowerNet == lampPower.PowerNet, "the top is on the platform's grid with the lamp");

                yield return new WaitUntil(() => lampPower.PowerOn && Mathf.Abs(up.delivered - lampWatts) < 1f, 900);
                ctx.Check(lampPower.PowerOn, "the lamp on the platform is powered from the water level");
                ctx.Check(Mathf.Abs(up.delivered - lampWatts) < 1f && Mathf.Abs(up.Trader.PowerOutput - lampWatts) < 1f, $"the top supplies exactly what the lamp draws ({up.Trader.PowerOutput:F0} W / {lampWatts:F0} W)");
                ctx.Check(Mathf.Abs(-down.Trader.PowerOutput - lampWatts / 0.95f) < 1f, $"the foot draws 5% more for line loss ({-down.Trader.PowerOutput:F0} W)");
                ctx.Check(down.CompInspectStringExtra().Contains(lampWatts.ToString("0")) && up.CompInspectStringExtra().Contains(lampWatts.ToString("0")), "both ends show the power going through");

                // 平台層加一顆空電池：多出來的容量拿去充電，但不超過 2000 W
                Thing platBattery = Build(ThingDef.Named("Battery"), p1 + new IntVec3(2, 0, -2), platform);
                CompPowerBattery platCell = platBattery.TryGetComp<CompPowerBattery>();
                platCell.DrawPower(platCell.StoredEnergy);
                yield return new WaitUntil(() => up.delivered > lampWatts + 1f, 300);
                links.Recompute();
                PowerNet sinkNet = up.Trader.PowerNet;
                ctx.Note($"sink batteries {sinkNet?.batteryComps.Count}, can accept {platCell.AmountCanAccept:F1}, stored {platCell.StoredEnergy:F1}, sources {links.Sources.Count}, partner {down.Partner == up}, footNet {down.Trader.PowerNet != null}, footStored {down.Trader.PowerNet?.CurrentStoredEnergy():F1}");
                ctx.Check(platCell.PowerNet == up.Trader.PowerNet, $"the platform battery is on the top's grid (spawned {platBattery.Spawned} at {platBattery.Position}, net {(platCell.PowerNet == null ? "none" : "other")}, terrain {platform.terrainGrid.TerrainAt(platBattery.Position)})");
                ctx.Check(Mathf.Abs(up.delivered - down.Props.capacity) < 1f, $"an empty battery takes the link to its capacity and no further ({up.delivered:F0} W)");
                ctx.Check(Mathf.Abs(-down.Trader.PowerOutput - down.Props.capacity / 0.95f) < 1f, $"the foot draws capacity plus loss ({-down.Trader.PowerOutput:F0} W)");
                yield return new WaitTicks(120);
                ctx.Check(platCell.StoredEnergy > 0.5f, $"the platform battery charges from the water level ({platCell.StoredEnergy:F1} Wd)");

                // 拆掉水面層的電池：送端沒有電可送
                seaBattery.Destroy();
                yield return new WaitTicks(5);
                links.Recompute();
                ctx.Check(up.delivered == 0f && up.Trader.PowerOutput == 0f, $"with nothing on the water level the top makes no power ({up.Trader.PowerOutput:F0} W)");
                ctx.Check(down.Trader.PowerOutput == 0f, "and the foot draws nothing");

                // 切換方向：平台層的電池（充滿）送下水面層的一盞燈
                Command_Action toggle = down.CompGetGizmosExtra().OfType<Command_Action>().FirstOrDefault();
                ctx.Require(toggle != null && up.CompGetGizmosExtra().OfType<Command_Action>().Any(), "both ends offer the direction toggle");
                toggle.action();
                ctx.Check(!down.IsSource && up.IsSource && down.reversed && up.reversed, "the toggle flips both ends: the top sends, the foot receives");
                platCell.AddEnergy(platCell.AmountCanAccept);
                Thing seaLamp = Build(ThingDef.Named("StandingLamp"), new IntVec3(dock.minX, 0, dock.minZ + 1), sea);
                CompPowerTrader seaLampPower = seaLamp.TryGetComp<CompPowerTrader>();
                float seaLampWatts = seaLampPower.Props.PowerConsumption;
                yield return new WaitUntil(() => seaLampPower.PowerOn && Mathf.Abs(down.delivered - seaLampWatts) < 1f, 900);
                upLinks.Recompute();
                links.Recompute();
                ctx.Check(seaLampPower.PowerOn, "the lamp on the water level is powered from the platform");
                ctx.Check(Mathf.Abs(down.Trader.PowerOutput - seaLampWatts) < 1f, $"the foot supplies what the sea lamp draws ({down.Trader.PowerOutput:F0} W / {seaLampWatts:F0} W)");
                ctx.Check(Mathf.Abs(-up.Trader.PowerOutput - seaLampWatts / 0.95f) < 1f, $"the top draws it plus line loss ({-up.Trader.PowerOutput:F0} W)");
                toggle.action();
                ctx.Check(down.IsSource && !up.IsSource, "toggling again restores the upward direction");

                // 拆支柱：橋接斷掉
                foot.Destroy(DestroyMode.Vanish);
                yield return new WaitFrames(3);
                ctx.Check(!links.Sources.Contains(down), "the removed pillar leaves the sea level's power links");
                ctx.Check(!top.Spawned, "and its top went with it");
            }
            finally
            {
                DebugSettings.unlimitedPower = wasUnlimited;
                foreach (Thing t in spawned.Where(t => !t.Destroyed).ToList()) { t.Destroy(); }
                Tests_SeaSurface.RemoveSeaMaps(sea);
            }
            yield return new WaitFrames(2);
        }
    }
}
