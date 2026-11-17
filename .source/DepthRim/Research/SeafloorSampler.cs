using System.Collections.Generic;
using Fortified;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary><see cref="CompProperties_SeafloorSampler.sources"/> 的一項：能被採樣的海底地標與它總共能給出的研究點數。</summary>
    public class SeafloorResearchSource
    {
        public ThingDef thing;

        public float totalPoints = 2000f;
    }

    public class CompProperties_SeafloorSampler : CompProperties
    {
        /// <summary>產出的知識類別（FFF 知識分頁：海底研究 DepthRim_Seabed）。</summary>
        public KnowledgeCategoryDef knowledgeCategory;

        /// <summary>每天（60000 tick）從地標提取的研究點數（套用難度的研究速度倍率前）。</summary>
        public float pointsPerDay = 360f;

        public List<SeafloorResearchSource> sources = new List<SeafloorResearchSource>();

        public CompProperties_SeafloorSampler()
        {
            compClass = typeof(CompSeafloorSampler);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef))
            {
                yield return e;
            }
            if (knowledgeCategory == null)
            {
                yield return "CompProperties_SeafloorSampler.knowledgeCategory is not set";
            }
        }

        public SeafloorResearchSource SourceFor(ThingDef def)
        {
            if (def == null)
            {
                return null;
            }
            foreach (SeafloorResearchSource s in sources)
            {
                if (s.thing == def)
                {
                    return s;
                }
            }
            return null;
        }
    }

    /// <summary>純計算（單元測試用）。</summary>
    public static class SeafloorSamplerMath
    {
        /// <summary>
        /// 一次結算：本次想提取 <paramref name="want"/> 點，受地標剩餘量限制。
        /// 回傳要注入的知識點數（已乘上 <paramref name="speedFactor"/>），<paramref name="drawn"/> 是從地標扣掉的量。
        /// 專案之間的溢流交給 FFF（<see cref="KnowledgeUtility.AddKnowledge"/>）。
        /// </summary>
        public static float Step(float want, float sourceRemaining, float speedFactor, out float drawn)
        {
            drawn = 0f;
            if (want <= 0f || sourceRemaining <= 0f || speedFactor <= 0f)
            {
                return 0f;
            }
            drawn = Mathf.Min(want, sourceRemaining);
            return drawn * speedFactor;
        }
    }

    /// <summary>
    /// 海底採樣器（2×1）：緊貼冷泉、海底火山或熱液泉，通電時持續把地標的海底研究點數（FFF 知識類別）注入「海底研究」分頁。
    /// 每個地標的總量記在 <see cref="MapComponent_SeafloorResearch"/>（同一個地標旁蓋多台會一起分完同一份）。
    /// 熱液泉暫用原版蒸汽間歇泉，所以不用 FFF 的 CompOccultechSource（要把 comp 加到每一個原版間歇泉上），自己記帳。
    /// 只在海洋地圖上運作；分頁沒有可推進的專案、沒有可採的地標時只吃待機電力。
    /// </summary>
    public class CompSeafloorSampler : ThingComp
    {
        public const int Interval = GenTicks.TickRareInterval;

        private CompPowerTrader power;

        private CompFlickable flickable;

        private Thing source;

        public CompProperties_SeafloorSampler Props => (CompProperties_SeafloorSampler)props;

        public Thing Source => source;

        private bool PoweredOn => parent.Spawned && (power == null || power.PowerOn) && (flickable == null || flickable.SwitchIsOn);

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            power = parent.GetComp<CompPowerTrader>();
            flickable = parent.GetComp<CompFlickable>();
            source = FindSource(parent.def, parent.Position, parent.Rotation, parent.Map, Props);
            IdlePowerUtility.SetBusy(power, HasWork);
        }

        /// <summary>緊貼佔地（含斜角）的來源地標中剩最多的一個；不在海洋地圖時沒有。</summary>
        public static Thing FindSource(ThingDef def, IntVec3 center, Rot4 rot, Map map, CompProperties_SeafloorSampler props)
        {
            if (map == null || props == null || !FloodingUtility.IsWaterMap(map))
            {
                return null;
            }
            MapComponent_SeafloorResearch ledger = map.GetComponent<MapComponent_SeafloorResearch>();
            Thing best = null;
            float bestLeft = -1f;
            foreach (IntVec3 c in GenAdj.OccupiedRect(center, rot, def.size).ExpandedBy(1))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    SeafloorResearchSource s = props.SourceFor(t.def);
                    if (s == null)
                    {
                        continue;
                    }
                    float left = ledger?.Remaining(t, s.totalPoints) ?? s.totalPoints;
                    if (left > bestLeft)
                    {
                        best = t;
                        bestLeft = left;
                    }
                }
            }
            return best;
        }

        private MapComponent_SeafloorResearch Ledger => parent.Map?.GetComponent<MapComponent_SeafloorResearch>();

        public float SourceRemaining
        {
            get
            {
                SeafloorResearchSource s = source != null ? Props.SourceFor(source.def) : null;
                if (s == null)
                {
                    return 0f;
                }
                return Ledger?.Remaining(source, s.totalPoints) ?? 0f;
            }
        }

        /// <summary>海底研究分頁還有可推進的專案（有 Anomaly 時要在分頁裡選了專案）。</summary>
        public bool HasResearchTarget => Find.ResearchManager != null && KnowledgeUtility.HasResearchTarget(Props.knowledgeCategory);

        private bool HasWork => source != null && SourceRemaining > 0f && HasResearchTarget;

        public override void CompTickRare()
        {
            base.CompTickRare();
            // 來源被摧毀、或已採完時看看旁邊還有沒有別的
            if (source == null || !source.Spawned || source.Map != parent.Map || SourceRemaining <= 0f)
            {
                source = FindSource(parent.def, parent.Position, parent.Rotation, parent.Map, Props);
            }
            IdlePowerUtility.SetBusy(power, HasWork);
            if (!PoweredOn || !HasWork)
            {
                return;
            }
            Extract(Interval);
        }

        /// <summary>結算 <paramref name="ticks"/> tick 的提取量（測試也直接呼叫）。回傳注入的知識點數。</summary>
        public float Extract(int ticks)
        {
            SeafloorResearchSource s = source != null ? Props.SourceFor(source.def) : null;
            MapComponent_SeafloorResearch ledger = Ledger;
            if (s == null || ledger == null || !HasResearchTarget)
            {
                return 0f;
            }
            float speed = Find.Storyteller?.difficulty?.researchSpeedFactor ?? 1f;
            float want = Props.pointsPerDay * ticks / GenDate.TicksPerDay;
            float left = ledger.Remaining(source, s.totalPoints);
            float gained = SeafloorSamplerMath.Step(want, left, speed, out float drawn);
            if (drawn <= 0f)
            {
                return 0f;
            }
            ledger.Draw(source, drawn);
            KnowledgeUtility.AddKnowledge(Props.knowledgeCategory, gained);
            if (ledger.Remaining(source, s.totalPoints) <= 0f)
            {
                Messages.Message("DepthRim.Sampler.Exhausted".Translate(source.LabelCap), new TargetInfo(source.Position, parent.Map), MessageTypeDefOf.NeutralEvent);
            }
            return gained;
        }

        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned)
            {
                return null;
            }
            if (!FloodingUtility.IsWaterMap(parent.Map))
            {
                return "DepthRim.Sampler.NotOcean".Translate();
            }
            if (source == null)
            {
                return "DepthRim.Sampler.NoSource".Translate();
            }
            float left = SourceRemaining;
            if (left <= 0f)
            {
                return "DepthRim.Sampler.SourceExhausted".Translate(source.LabelCap);
            }
            string text = "DepthRim.Sampler.Sampling".Translate(source.LabelCap, Mathf.CeilToInt(left));
            if (!HasResearchTarget)
            {
                text += "\n" + "DepthRim.Sampler.NoProject".Translate();
            }
            else if (PoweredOn)
            {
                float speed = Find.Storyteller?.difficulty?.researchSpeedFactor ?? 1f;
                text += "\n" + "DepthRim.Sampler.Rate".Translate(Mathf.RoundToInt(Props.pointsPerDay * speed));
            }
            return text;
        }
    }

    /// <summary>每個地標已被採走多少研究點數（以 thingIDNumber 記，存檔安全；地標被摧毀後殘留的鍵在存檔時清掉）。</summary>
    public class MapComponent_SeafloorResearch : MapComponent
    {
        private Dictionary<int, float> extracted = new Dictionary<int, float>();

        public MapComponent_SeafloorResearch(Map map) : base(map)
        {
        }

        public float Remaining(Thing t, float total)
        {
            if (t == null)
            {
                return 0f;
            }
            extracted.TryGetValue(t.thingIDNumber, out float used);
            // 浮點誤差留下的零頭當作採完，否則採樣器會一直卡在幾乎是零的地標上、不換下一個
            float left = total - used;
            return left < 0.01f ? 0f : left;
        }

        public void Draw(Thing t, float amount)
        {
            if (t == null || amount <= 0f)
            {
                return;
            }
            extracted.TryGetValue(t.thingIDNumber, out float used);
            extracted[t.thingIDNumber] = used + amount;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                HashSet<int> alive = new HashSet<int>();
                foreach (Thing t in map.listerThings.AllThings)
                {
                    alive.Add(t.thingIDNumber);
                }
                List<int> dead = new List<int>();
                foreach (int id in extracted.Keys)
                {
                    if (!alive.Contains(id))
                    {
                        dead.Add(id);
                    }
                }
                foreach (int id in dead)
                {
                    extracted.Remove(id);
                }
            }
            Scribe_Collections.Look(ref extracted, "extracted", LookMode.Value, LookMode.Value);
            if (extracted == null)
            {
                extracted = new Dictionary<int, float>();
            }
        }
    }

    /// <summary>海底採樣器只能蓋在海洋地圖上、緊貼（含斜角）海底火山或熱液泉，不能壓在地標上。放置時框出附近的地標。</summary>
    public class PlaceWorker_SeafloorSampler : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            CompProperties_SeafloorSampler props = (checkingDef as ThingDef)?.GetCompProperties<CompProperties_SeafloorSampler>();
            if (props == null)
            {
                return true;
            }
            if (!FloodingUtility.IsWaterMap(map))
            {
                return "DepthRim.Sampler.MustBeOcean".Translate();
            }
            CellRect rect = GenAdj.OccupiedRect(loc, rot, checkingDef.Size);
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map))
                {
                    if (props.SourceFor(t.def) != null)
                    {
                        return "DepthRim.Sampler.OnSource".Translate();
                    }
                }
            }
            if (CompSeafloorSampler.FindSource((ThingDef)checkingDef, loc, rot, map, props) == null)
            {
                return "DepthRim.Sampler.MustBeAdjacent".Translate();
            }
            return true;
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map map = Find.CurrentMap;
            CompProperties_SeafloorSampler props = def.GetCompProperties<CompProperties_SeafloorSampler>();
            if (map == null || props == null || !FloodingUtility.IsWaterMap(map))
            {
                return;
            }
            Thing target = CompSeafloorSampler.FindSource(def, center, rot, map, props);
            if (target != null)
            {
                GenDraw.DrawFieldEdges(new List<IntVec3>(target.OccupiedRect().Cells), Color.cyan);
                SeafloorResearchSource s = props.SourceFor(target.def);
                float left = map.GetComponent<MapComponent_SeafloorResearch>()?.Remaining(target, s.totalPoints) ?? s.totalPoints;
                Widgets.MouseAttachedLabel("DepthRim.Sampler.GhostRemaining".Translate(target.LabelCap, Mathf.CeilToInt(left)));
            }
        }
    }
}
