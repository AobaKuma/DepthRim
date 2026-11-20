using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 跨層電力的純計算（Docs/海岸船舶與潛水鐘_機制設計.md §7.10，M5.5）。單位都是瓦。
    /// 單向：送端（例如水面層的支柱腳）把它所在電網的剩餘電力送到收端（平台層的支柱頂），容量上限、線損照比例。
    /// </summary>
    public static class CrossLayerMath
    {
        /// <summary>一個電網上的一個用電／發電元件（跨層橋接本身不算）。</summary>
        public struct Part
        {
            /// <summary>原版 <c>CompPowerTrader.PowerOutput</c>：正的是發電、負的是用電。</summary>
            public float output;
            /// <summary>現在通電（原版 <c>PowerOn</c>）。</summary>
            public bool on;
            /// <summary>沒電但想開（開關打開、沒有故障）——原版有剩餘電力時會把它打開，所以收端要先把它算進需求。</summary>
            public bool wantsOn;

            public Part(float output, bool on, bool wantsOn)
            {
                this.output = output;
                this.on = on;
                this.wantsOn = wantsOn;
            }
        }

        /// <summary>
        /// 收端電網缺多少電：所有開著或想開的用電，減掉開著的發電；不會是負的。
        /// 電網上的電池還沒充滿時再加 <paramref name="chargeAllowance"/>（讓多出來的容量拿去充電）。
        /// </summary>
        public static float Need(IEnumerable<Part> parts, bool batteryWantsCharge, float chargeAllowance)
        {
            float load = 0f, gen = 0f;
            foreach (Part p in parts)
            {
                if (p.output < 0f && (p.on || p.wantsOn))
                {
                    load -= p.output;
                }
                else if (p.output > 0f && p.on)
                {
                    gen += p.output;
                }
            }
            float need = Math.Max(0f, load - gen);
            return batteryWantsCharge ? need + Math.Max(0f, chargeAllowance) : need;
        }

        /// <summary>
        /// 送端電網能拿出多少：開著的發電減開著的用電（原版 <c>CurrentEnergyGainRate</c> 的算法）；
        /// 電網上的電池還有電就當作不受限（<see cref="float.PositiveInfinity"/>，之後由容量卡住）。
        /// </summary>
        public static float Surplus(IEnumerable<Part> parts, bool hasStoredEnergy)
        {
            if (hasStoredEnergy)
            {
                return float.PositiveInfinity;
            }
            float net = 0f;
            foreach (Part p in parts)
            {
                if (p.on)
                {
                    net += p.output;
                }
            }
            return Math.Max(0f, net);
        }

        /// <summary>送到對面 <paramref name="delivered"/> 瓦，送端要抽多少（含線損）。</summary>
        public static float Draw(float delivered, float lineLoss)
        {
            return delivered <= 0f ? 0f : delivered / (1f - Clamp01Loss(lineLoss));
        }

        /// <summary>
        /// 同一個收端電網上的幾條橋接一起分配（依序，先到先拿）：每條最多 <c>capacity</c>、總共最多 <paramref name="need"/>；
        /// 每條的送端電網剩多少（<paramref name="surplus"/>，以送端電網編號為鍵，會被扣掉）扣完線損後也是上限。回傳每條送到對面的瓦數。
        /// </summary>
        public static float[] Allocate(float need, IList<(int sourceGroup, float capacity)> links, Dictionary<int, float> surplus, float lineLoss)
        {
            float keep = 1f - Clamp01Loss(lineLoss);
            float[] delivered = new float[links.Count];
            float left = Math.Max(0f, need);
            for (int i = 0; i < links.Count && left > 0f; i++)
            {
                (int group, float capacity) = links[i];
                surplus.TryGetValue(group, out float avail);
                float t = Math.Min(Math.Min(Math.Max(0f, capacity), left), avail * keep);
                if (t <= 0f || float.IsNaN(t))
                {
                    continue;
                }
                delivered[i] = t;
                left -= t;
                if (!float.IsPositiveInfinity(avail))
                {
                    surplus[group] = Math.Max(0f, avail - t / keep);
                }
            }
            return delivered;
        }

        private static float Clamp01Loss(float loss)
        {
            return loss < 0f ? 0f : (loss > 0.95f ? 0.95f : loss);
        }
    }

    /// <summary>跨層電力橋接的設定：掛在兩端（同一個 ThingDef 結構的上下兩端），兩端都還要有原版的 <c>CompProperties_Power</c>（CompPowerTrader，耗電 0）。</summary>
    public class CompProperties_CrossLayerPower : CompProperties
    {
        /// <summary>最多送幾瓦到對面（送端抽的電另加線損）。</summary>
        public float capacity = 2000f;

        /// <summary>線損比例：送到對面 X 瓦，送端要抽 X ÷ (1 − lineLoss)。</summary>
        public float lineLoss = 0.05f;

        public CompProperties_CrossLayerPower()
        {
            compClass = typeof(CompCrossLayerPower);
        }
    }

    /// <summary>
    /// 跨層電力橋接（§7.10）：RimWorld 的 <c>PowerNet</c> 是每張地圖各一個，沒有跨圖輸電，所以兩端各是自己地圖電網上的一個普通
    /// <c>CompPowerTrader</c>（原版的 <c>ConnectToPower</c> 只認這個確切的型別），這個元件改它們的輸出：
    /// <list type="bullet">
    /// <item>送端（<see cref="MapPortal"/>，例如水面層的支柱腳）：用電 = 送到對面的量 ÷ (1 − 線損)；</item>
    /// <item>收端（<see cref="PocketMapExit"/>，例如平台層的支柱頂）：發電 = 送到對面的量，只在送端通電時才有。</item>
    /// </list>
    /// 送多少由送端地圖的 <see cref="MapComponent_CrossLayerPower"/> 每 <see cref="MapComponent_CrossLayerPower.Interval"/> ticks 算一次：
    /// 收端電網缺多少（含想開的用電與沒充滿的電池），送端電網剩多少（有電池存電就不受限），容量上限。單向：收端的電不會送下去。
    /// 任一端被拆就斷（另一端歸零）。
    /// </summary>
    public class CompCrossLayerPower : ThingComp
    {
        /// <summary>最近一次送到對面（收端：收到）的瓦數，給檢視面板看；不存檔，載入後下一次計算就有。</summary>
        public float delivered;

        private CompPowerTrader trader;

        public CompProperties_CrossLayerPower Props => (CompProperties_CrossLayerPower)props;

        public CompPowerTrader Trader => trader ?? (trader = parent.GetComp<CompPowerTrader>());

        /// <summary>送端：上下兩端的下端（升降梯的入口）。<see cref="PocketMapExit"/> 本身也是 <see cref="MapPortal"/>，所以要先排除。</summary>
        public bool IsSource => parent is MapPortal && !(parent is PocketMapExit);

        /// <summary>另一端（還在地圖上的才算）。</summary>
        public CompCrossLayerPower Partner
        {
            get
            {
                Thing other = parent is PocketMapExit exit ? exit.entrance : (parent as MapPortal)?.exit;
                return other != null && other.Spawned ? other.TryGetComp<CompCrossLayerPower>() : null;
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            trader = parent.GetComp<CompPowerTrader>();
            if (IsSource)
            {
                parent.Map.GetComponent<MapComponent_CrossLayerPower>()?.Register(this);
            }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            map.GetComponent<MapComponent_CrossLayerPower>()?.Deregister(this);
            delivered = 0f;
            // 斷線：對面立刻歸零
            CompCrossLayerPower other = Partner;
            if (other != null)
            {
                other.Apply(0f, false);
            }
        }

        /// <summary>把這一端的電網元件設成送出（送端，抽 <see cref="CrossLayerMath.Draw"/>）或收到（收端）<paramref name="watts"/> 瓦。</summary>
        internal void Apply(float watts, bool sourcePowered)
        {
            CompPowerTrader t = Trader;
            if (t == null)
            {
                return;
            }
            if (IsSource)
            {
                delivered = watts;
                t.PowerOutput = -CrossLayerMath.Draw(watts, Props.lineLoss);
                return;
            }
            delivered = sourcePowered ? watts : 0f;
            t.PowerOutput = delivered;
            if (!t.PowerOn && FlickUtility.WantsToBeOn(parent) && !parent.IsBrokenDown())
            {
                t.PowerOn = true;
            }
        }

        public override string CompInspectStringExtra()
        {
            CompCrossLayerPower other = Partner;
            if (other == null)
            {
                return "DepthRim.CrossPower.NotLinked".Translate();
            }
            string cap = Props.capacity.ToString("0");
            string loss = (Props.lineLoss * 100f).ToString("0");
            if (IsSource)
            {
                return "DepthRim.CrossPower.Sending".Translate(delivered.ToString("0"), cap, loss);
            }
            if (other.Trader != null && !other.Trader.PowerOn)
            {
                return "DepthRim.CrossPower.SourceUnpowered".Translate();
            }
            return "DepthRim.CrossPower.Receiving".Translate(delivered.ToString("0"), cap);
        }
    }

    /// <summary>
    /// 每張地圖上的跨層電力分配（只管送端在這張地圖的橋接）：同一個收端電網上的橋接一起算，免得兩根支柱各自把整個缺口補一遍。
    /// </summary>
    public class MapComponent_CrossLayerPower : MapComponent
    {
        public const int Interval = 15;

        private readonly List<CompCrossLayerPower> sources = new List<CompCrossLayerPower>();

        public MapComponent_CrossLayerPower(Map map) : base(map)
        {
        }

        public IReadOnlyList<CompCrossLayerPower> Sources => sources;

        public void Register(CompCrossLayerPower c)
        {
            if (!sources.Contains(c))
            {
                sources.Add(c);
            }
        }

        public void Deregister(CompCrossLayerPower c)
        {
            sources.Remove(c);
        }

        public override void MapComponentTick()
        {
            if (sources.Count == 0 || (Find.TickManager.TicksGame + map.uniqueID) % Interval != 0)
            {
                return;
            }
            Recompute();
        }

        /// <summary>重算這張地圖上所有橋接（測試也直接呼叫）。</summary>
        public void Recompute()
        {
            HashSet<CompPowerTrader> bridges = new HashSet<CompPowerTrader>();
            List<(CompCrossLayerPower src, CompCrossLayerPower dst)> links = new List<(CompCrossLayerPower, CompCrossLayerPower)>();
            foreach (CompCrossLayerPower s in sources)
            {
                CompCrossLayerPower d = s.Partner;
                if (s.Trader != null) { bridges.Add(s.Trader); }
                if (d?.Trader != null) { bridges.Add(d.Trader); }
                if (d == null || d.Trader == null || s.Trader == null)
                {
                    s.Apply(0f, false);
                    continue;
                }
                links.Add((s, d));
            }

            // 送端電網剩多少（以電網為單位，多條橋接共用同一個送端電網時一起扣）
            Dictionary<PowerNet, int> netIds = new Dictionary<PowerNet, int>();
            Dictionary<int, float> surplus = new Dictionary<int, float>();
            int GroupOf(PowerNet net)
            {
                if (net == null)
                {
                    return -1;
                }
                if (!netIds.TryGetValue(net, out int id))
                {
                    id = netIds.Count;
                    netIds[net] = id;
                    surplus[id] = CrossLayerMath.Surplus(PartsOf(net, bridges), net.CurrentStoredEnergy() > 0.1f);
                }
                return id;
            }

            foreach (IGrouping<PowerNet, (CompCrossLayerPower src, CompCrossLayerPower dst)> group in links.GroupBy(l => l.dst.Trader.PowerNet))
            {
                List<(CompCrossLayerPower src, CompCrossLayerPower dst)> list = group.OrderBy(l => l.src.parent.thingIDNumber).ToList();
                PowerNet sink = group.Key;
                if (sink == null)
                {
                    foreach ((CompCrossLayerPower src, CompCrossLayerPower dst) l in list)
                    {
                        l.src.Apply(0f, false);
                        l.dst.Apply(0f, false);
                    }
                    continue;
                }
                float chargeAllowance = list.Sum(l => l.src.Props.capacity);
                bool batteryWants = sink.batteryComps.Any(b => b.AmountCanAccept > 0.01f);
                float need = CrossLayerMath.Need(PartsOf(sink, bridges), batteryWants, chargeAllowance);
                List<(int, float)> spec = list.Select(l => (GroupOf(l.src.Trader.PowerNet), l.src.Props.capacity)).ToList();
                float lineLoss = list[0].src.Props.lineLoss;
                float[] delivered = CrossLayerMath.Allocate(need, spec, surplus, lineLoss);
                for (int i = 0; i < list.Count; i++)
                {
                    list[i].src.Apply(delivered[i], false);
                    list[i].dst.Apply(delivered[i], list[i].src.Trader.PowerOn);
                }
            }
        }

        private static IEnumerable<CrossLayerMath.Part> PartsOf(PowerNet net, HashSet<CompPowerTrader> exclude)
        {
            foreach (CompPowerTrader c in net.powerComps)
            {
                if (exclude.Contains(c))
                {
                    continue;
                }
                yield return new CrossLayerMath.Part(c.PowerOutput, c.PowerOn, !c.PowerOn && FlickUtility.WantsToBeOn(c.parent) && !c.parent.IsBrokenDown());
            }
        }
    }
}
