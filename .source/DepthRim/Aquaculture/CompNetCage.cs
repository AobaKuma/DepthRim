using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    public class CompProperties_NetCage : CompProperties
    {
        public NetLayer layer = NetLayer.Shallow;

        /// <summary>網袋中點的深度（m）。</summary>
        public float netDepth = 10f;

        /// <summary>放置門檻：水深至少這麼深。</summary>
        public float minWaterDepth = 10f;

        /// <summary>外海惡劣海況時的成長倍率。</summary>
        public float stormGrowthFactor = 0.5f;

        /// <summary>海況傷害倍率（7×7 表面積大）。</summary>
        public float stormDamageFactor = 1.5f;

        public float capacityKg = NetCageMath.CapacityKg;

        public float feedCapacity = NetCageMath.FeedCapacity;

        /// <summary>只能放在外海（斜溫層／深層網）。</summary>
        public bool openSeaOnly;

        public Color tint = Color.white;

        public CompProperties_NetCage()
        {
            compClass = typeof(CompNetCage);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef))
            {
                yield return e;
            }
            if (netDepth <= 0f || minWaterDepth <= 0f || capacityKg <= 0f || feedCapacity <= 0f || stormDamageFactor <= 0f)
            {
                yield return "netDepth, minWaterDepth, capacityKg, feedCapacity and stormDamageFactor must be positive";
            }
            if (minWaterDepth < netDepth)
            {
                yield return "minWaterDepth must be at least netDepth";
            }
        }
    }

    /// <summary>
    /// 箱網的魚群資料與每 250 tick 的推進（§4.2、§5）。魚是資料，不是 Pawn；只有逃逸會生成 Pawn。
    /// 物料（Kibble、修網用的布與鋼、藥品）由工作搬進來存在箱網裡，不是儲存區。
    /// </summary>
    public class CompNetCage : ThingComp
    {
        private AquacultureSpeciesDef species;
        private int count;
        private int stockedCount;
        private float avgWeight;
        private float feedStore;
        private float diseaseSeverity;
        private int lastSpreadCheck = -9999999;
        private int isolatedDays;
        private float feedRateSetting = 1f;
        private int lowOxygenTicks;
        private int lethalOxygenTicks;
        private int ticksSinceStocked;
        private int ticksAtHarvestWeight;
        private int ticksSinceFed = 9999999;
        private int lastBreachMessageTick = -9999999;
        private float deathAccumulator;
        private int dayCounter;

        // 工作指令
        private bool harvestOrdered;
        private bool emergencyHarvest;
        private bool replaceNetOrdered;
        private AquacultureSpeciesDef pendingSpecies;
        private int pendingCount;

        // 搬進來的物料
        private int clothStored;
        private int steelStored;
        private int medicineStored;
        private int herbalStored;

        // 不存檔：每 250 tick 重算
        private float oxygen = 1f;
        private float waterTemp = 10f;
        private CageEnvironment env;

        // 給測試看
        public float TotalFeedEaten { get; private set; }
        public int TotalDied { get; private set; }
        public int TotalEscaped { get; private set; }
        public int EscapePawnsSpawned { get; private set; }

        public static bool debugAlwaysBreach;

        /// <summary>測試用：固定溶氧（驗收測試把溶氧壓到缺氧）。</summary>
        public static float? debugOxygen;

        public CompProperties_NetCage Props => (CompProperties_NetCage)props;

        public Building_NetCage Cage => parent as Building_NetCage;

        public AquacultureSpeciesDef Species => species;
        public int Count => count;
        public int StockedCount => stockedCount;
        public float AvgWeight => avgWeight;
        public float FeedStore => feedStore;
        public float Oxygen => oxygen;
        public float WaterTemp => waterTemp;
        public CageEnvironment Environment => env;
        public float FeedRateSetting { get => feedRateSetting; set => feedRateSetting = value; }
        public int TicksSinceFed => ticksSinceFed;
        public int LowOxygenTicks => lowOxygenTicks;
        public bool HasFish => species != null && count > 0;
        public float Biomass => HasFish ? count * avgWeight : 0f;
        public float Density => NetCageMath.Density(Biomass);
        public float WeightRatio => HasFish ? avgWeight / species.harvestWeight : 0f;
        public bool Ready => HasFish && WeightRatio >= NetCageMath.HarvestReady;
        public float DaysAtHarvestWeight => ticksAtHarvestWeight / (float)GenDate.TicksPerDay;
        public float OverripeDays => NetCageMath.OverripeDays(DaysAtHarvestWeight);
        public float Integrity => parent.MaxHitPoints > 0 ? parent.HitPoints / (float)parent.MaxHitPoints : 1f;
        public float DiseaseSeverity => diseaseSeverity;
        public int LastSpreadCheck { get => lastSpreadCheck; set => lastSpreadCheck = value; }
        public bool HarvestOrdered => harvestOrdered;
        public bool EmergencyHarvestOrdered => emergencyHarvest;
        public bool ReplaceNetOrdered => replaceNetOrdered;
        public AquacultureSpeciesDef PendingSpecies => pendingSpecies;
        public int PendingCount => pendingCount;
        public bool SustainedLowOxygen => lowOxygenTicks >= NetCageMath.LowOxygenTicks;

        public MapComponent_Aquaculture MapComp => MapComponent_Aquaculture.For(parent.Map);

        public CageVenue Venue => MapComp?.Venue ?? CageVenue.Bay;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Defs.Look(ref species, "species");
            Scribe_Values.Look(ref count, "count");
            Scribe_Values.Look(ref stockedCount, "stockedCount");
            Scribe_Values.Look(ref avgWeight, "avgWeight");
            Scribe_Values.Look(ref feedStore, "feedStore");
            Scribe_Values.Look(ref diseaseSeverity, "diseaseSeverity");
            Scribe_Values.Look(ref lastSpreadCheck, "lastSpreadCheck", -9999999);
            Scribe_Values.Look(ref isolatedDays, "isolatedDays");
            Scribe_Values.Look(ref feedRateSetting, "feedRateSetting", 1f);
            Scribe_Values.Look(ref lowOxygenTicks, "lowOxygenTicks");
            Scribe_Values.Look(ref lethalOxygenTicks, "lethalOxygenTicks");
            Scribe_Values.Look(ref ticksSinceStocked, "ticksSinceStocked");
            Scribe_Values.Look(ref ticksAtHarvestWeight, "ticksAtHarvestWeight");
            Scribe_Values.Look(ref ticksSinceFed, "ticksSinceFed", 9999999);
            Scribe_Values.Look(ref lastBreachMessageTick, "lastBreachMessageTick", -9999999);
            Scribe_Values.Look(ref deathAccumulator, "deathAccumulator");
            Scribe_Values.Look(ref dayCounter, "dayCounter");
            Scribe_Values.Look(ref harvestOrdered, "harvestOrdered");
            Scribe_Values.Look(ref emergencyHarvest, "emergencyHarvest");
            Scribe_Values.Look(ref replaceNetOrdered, "replaceNetOrdered");
            Scribe_Defs.Look(ref pendingSpecies, "pendingSpecies");
            Scribe_Values.Look(ref pendingCount, "pendingCount");
            Scribe_Values.Look(ref clothStored, "clothStored");
            Scribe_Values.Look(ref steelStored, "steelStored");
            Scribe_Values.Look(ref medicineStored, "medicineStored");
            Scribe_Values.Look(ref herbalStored, "herbalStored");
        }

        // ── 放養 ──

        public float WaterDepth => MapComp?.WaterDepthAt(parent.OccupiedRect()) ?? NetCageMath.BayDeepDepth;

        public float CurrentWaterTemp()
        {
            MapComponent_Aquaculture comp = MapComp;
            if (comp == null) { return waterTemp; }
            return comp.WaterTemperatureAt(Props.netDepth) + ClimateMath.CurrentOffset(comp.Current, Props.netDepth, comp.Venue);
        }

        public string CannotStockReason(AquacultureSpeciesDef def)
        {
            return def.CannotStockReason(Venue, Props.layer, CurrentWaterTemp());
        }

        public void OrderStock(AquacultureSpeciesDef def, int fishCount)
        {
            pendingSpecies = def;
            pendingCount = fishCount;
        }

        public void CancelStock()
        {
            pendingSpecies = null;
            pendingCount = 0;
        }

        /// <summary>直接放養（工作完成時、或測試）。</summary>
        public void Stock(AquacultureSpeciesDef def, int fishCount)
        {
            ClearFish();
            species = def;
            count = stockedCount = Mathf.Max(1, fishCount);
            avgWeight = NetCageMath.StartWeightFraction * def.harvestWeight;
            ticksSinceStocked = 0;
            ticksAtHarvestWeight = 0;
            lowOxygenTicks = lethalOxygenTicks = 0;
            deathAccumulator = 0f;
            diseaseSeverity = 0f;
            isolatedDays = 0;
            CancelStock();
        }

        public void ClearFish()
        {
            species = null;
            count = stockedCount = 0;
            avgWeight = 0f;
            diseaseSeverity = 0f;
            harvestOrdered = emergencyHarvest = false;
            ticksAtHarvestWeight = 0;
            lowOxygenTicks = lethalOxygenTicks = 0;
        }

        // ── 物料 ──

        public float FeedSpace => Mathf.Max(0f, Props.feedCapacity - feedStore);

        public bool NeedsFeed => HasFish && feedStore < Props.feedCapacity * NetCageMath.RefillFraction;

        public const int RepairCloth = 45;
        public const int RepairSteel = 30;
        public const int RepairHp = 90;
        public const int ReplaceCloth = 75;

        public bool NeedsRepair => Integrity < NetCageMath.BreachIntegrity && !replaceNetOrdered;

        public bool CanReplaceNet => Integrity < NetCageMath.BreachIntegrity;

        public int ClothNeeded => replaceNetOrdered ? Mathf.Max(0, ReplaceCloth - clothStored) : Mathf.Max(0, RepairCloth - clothStored);

        public int SteelNeeded => replaceNetOrdered ? 0 : Mathf.Max(0, RepairSteel - steelStored);

        public bool NeedsTreatment => HasFish && diseaseSeverity > 0f;

        public int MedicineNeeded => medicineStored + herbalStored >= DiseaseSpread.MedicineCount ? 0 : DiseaseSpread.MedicineCount - medicineStored - herbalStored;

        /// <summary>這座箱網目前收不收這種物料、還要幾個。</summary>
        public int WantedCount(ThingDef def)
        {
            if (def == ThingDefOf.Kibble)
            {
                return HasFish ? Mathf.FloorToInt(FeedSpace) : 0;
            }
            if (def == ThingDefOf.Cloth)
            {
                return (NeedsRepair || replaceNetOrdered) ? ClothNeeded : 0;
            }
            if (def == ThingDefOf.Steel)
            {
                return NeedsRepair ? SteelNeeded : 0;
            }
            if (def.IsMedicine)
            {
                return NeedsTreatment ? MedicineNeeded : 0;
            }
            return 0;
        }

        /// <summary>收下搬來的物料，回傳收了幾個（多的留在搬運者手上）。</summary>
        public int Deposit(Thing thing)
        {
            int take = Mathf.Min(thing.stackCount, WantedCount(thing.def));
            if (take <= 0) { return 0; }
            if (thing.def == ThingDefOf.Kibble) { feedStore += take; }
            else if (thing.def == ThingDefOf.Cloth) { clothStored += take; }
            else if (thing.def == ThingDefOf.Steel) { steelStored += take; }
            else if (thing.def == ThingDefOf.MedicineHerbal) { herbalStored += take; }
            else if (thing.def.IsMedicine) { medicineStored += take; }
            thing.SplitOff(take).Destroy();
            return take;
        }

        public void AddFeed(float amount)
        {
            feedStore = Mathf.Clamp(feedStore + amount, 0f, Props.feedCapacity);
        }

        // ── 工作完成 ──

        public bool CanDoRepairWork => NeedsRepair && clothStored >= RepairCloth && steelStored >= RepairSteel;

        public bool CanDoReplaceWork => replaceNetOrdered && clothStored >= ReplaceCloth;

        public bool CanDoTreatWork => NeedsTreatment && medicineStored + herbalStored >= DiseaseSpread.MedicineCount;

        public void FinishRepair()
        {
            clothStored -= RepairCloth;
            steelStored -= RepairSteel;
            parent.HitPoints = Mathf.Min(parent.MaxHitPoints, parent.HitPoints + RepairHp);
            parent.Map?.listerBuildingsRepairable.Notify_BuildingRepaired((Building)parent);
        }

        public void FinishReplaceNet()
        {
            clothStored -= ReplaceCloth;
            replaceNetOrdered = false;
            parent.HitPoints = parent.MaxHitPoints;
            // 舊網回收一半的布
            if (parent.Spawned)
            {
                Thing refund = ThingMaker.MakeThing(ThingDefOf.Cloth);
                refund.stackCount = ReplaceCloth / 2;
                GenPlace.TryPlaceThing(refund, Cage.AccessCell(), parent.Map, ThingPlaceMode.Near);
            }
        }

        public void FinishTreatment()
        {
            // 先用一般藥品；不夠才補草藥，用到草藥就算草藥療效（×0.5）
            int fromMed = Mathf.Min(medicineStored, DiseaseSpread.MedicineCount);
            medicineStored -= fromMed;
            int fromHerbal = DiseaseSpread.MedicineCount - fromMed;
            herbalStored -= fromHerbal;
            bool herbal = fromHerbal > 0;
            diseaseSeverity = DiseaseSpread.AfterTreatment(diseaseSeverity, herbal);
        }

        public void Infect()
        {
            if (HasFish && diseaseSeverity <= 0f)
            {
                diseaseSeverity = DiseaseSpread.StartSeverity;
                isolatedDays = 0;
            }
        }

        public bool WantsHarvest => HasFish && ((harvestOrdered && Ready) || (emergencyHarvest && WeightRatio >= NetCageMath.EmergencyMin));

        /// <summary>收成：魚獲掉在互動格附近，箱網清空（病害一起移除）。回傳魚獲數。</summary>
        public int Harvest(IntVec3 dropAt)
        {
            if (!HasFish) { return 0; }
            int total = NetCageMath.HarvestYield(count, species.AverageYield, Mathf.Min(1f, WeightRatio), OverripeDays);
            ThingDef catchDef = species.CatchDef;
            if (catchDef != null && parent.Spawned)
            {
                int remaining = total;
                while (remaining > 0)
                {
                    Thing item = ThingMaker.MakeThing(catchDef);
                    item.stackCount = Mathf.Min(remaining, catchDef.stackLimit);
                    remaining -= item.stackCount;
                    GenPlace.TryPlaceThing(item, dropAt, parent.Map, ThingPlaceMode.Near);
                }
            }
            ClearFish();
            return total;
        }

        // ── 每 250 tick ──

        public const int TickInterval = 250;

        public override void CompTickRare()
        {
            base.CompTickRare();
            Advance(TickInterval);
        }

        /// <summary>推進 <paramref name="ticks"/>（測試可直接呼叫）。</summary>
        public void Advance(int ticks)
        {
            MapComponent_Aquaculture comp = MapComp;
            if (comp == null || !parent.Spawned || !MapComponent_Aquaculture.Enabled)
            {
                return;
            }
            ticksSinceFed += ticks;
            FloatRange temp = species?.Temperature ?? new FloatRange(0f, 20f);
            env = comp.EnvironmentFor(Cage, Props.netDepth, temp);
            waterTemp = env.waterTemp;
            if (!HasFish)
            {
                oxygen = NetCageMath.Oxygen(Props.netDepth, waterTemp, 0f, 1f, env.neighbors, env.aerators, env.bloom, env.oxygenExtra);
                return;
            }
            float dayFrac = ticks / (float)GenDate.TicksPerDay;
            ticksSinceStocked += ticks;
            float biomass = Biomass;
            // 降低餵食也降低溶氧需求
            float demandScale = 0.5f + 0.5f * feedRateSetting;
            oxygen = NetCageMath.Oxygen(Props.netDepth, waterTemp, biomass * demandScale, species.o2Demand, env.neighbors, env.aerators, env.bloom, env.oxygenExtra);
            if (debugOxygen.HasValue) { oxygen = debugOxygen.Value; }

            lowOxygenTicks = oxygen < NetCageMath.OxygenLow ? lowOxygenTicks + ticks : 0;
            lethalOxygenTicks = oxygen < NetCageMath.OxygenLethal ? lethalOxygenTicks + ticks : 0;
            if (lethalOxygenTicks >= NetCageMath.LowOxygenTicks)
            {
                lethalOxygenTicks = 0;
                HypoxiaKill();
                if (!HasFish) { return; }
            }

            // 飼料
            float need = NetCageMath.DailyFeed(biomass, species.feedRate, feedRateSetting) * dayFrac;
            bool fed = need > 0f && feedStore > 0f;
            if (fed)
            {
                float eaten = Mathf.Min(need, feedStore);
                feedStore -= eaten;
                TotalFeedEaten += eaten;
                ticksSinceFed = 0;
            }

            // 成長
            float tempDistance = species.TemperatureDistance(waterTemp);
            float growth = NetCageMath.GrowthPerDay(species.harvestWeight, species.growDays, tempDistance, oxygen, SustainedLowOxygen, Density, fed, env.growthFactor * feedRateSetting);
            avgWeight = Mathf.Min(species.harvestWeight, avgWeight + growth * dayFrac);
            if (avgWeight >= species.harvestWeight - 0.0001f)
            {
                ticksAtHarvestWeight += ticks;
            }

            // 死亡
            float mortality = NetCageMath.DailyMortality(oxygen, Density, tempDistance, OverripeDays);
            if (SustainedLowOxygen) { mortality += 0.02f; }
            deathAccumulator += count * mortality * dayFrac;
            if (deathAccumulator >= 1f)
            {
                int dead = Mathf.Min(count, Mathf.FloorToInt(deathAccumulator));
                deathAccumulator -= dead;
                Kill(dead);
            }

            // 每日判定
            dayCounter += ticks;
            if (dayCounter >= GenDate.TicksPerDay)
            {
                dayCounter -= GenDate.TicksPerDay;
                DailyChecks();
            }

            // 病害發生
            if (diseaseSeverity <= 0f && Rand.MTBEventOccurs(DiseaseSpread.OutbreakMtb(env.venue, waterTemp, oxygen, Density, species.diseaseResistance), GenDate.TicksPerDay, ticks))
            {
                Infect();
            }

            CheckFailed();
        }

        private void DailyChecks()
        {
            if (!HasFish) { return; }
            float chance = NetCageMath.DailyBreachChance(Integrity, Venue);
            if (debugAlwaysBreach || (chance > 0f && Rand.Chance(chance)))
            {
                Breach(Rand.Range(NetCageMath.BreachLossRange.x, NetCageMath.BreachLossRange.y));
            }
            if (Venue == CageVenue.OpenSea && Integrity < NetCageMath.DangerIntegrity && Rand.Chance(0.05f))
            {
                Kill(Mathf.Max(1, Mathf.RoundToInt(count * 0.03f)));
            }
        }

        /// <summary>病害每日：上升，或孤立 10 天後自癒（由地圖元件每天叫一次）。</summary>
        public void DiseaseDay(int neighbors)
        {
            if (diseaseSeverity <= 0f) { isolatedDays = 0; return; }
            isolatedDays = neighbors == 0 ? isolatedDays + 1 : 0;
            diseaseSeverity = DiseaseSpread.AfterIsolatedDay(diseaseSeverity, isolatedDays);
        }

        public void SetDisease(float severity)
        {
            diseaseSeverity = Mathf.Clamp01(severity);
        }

        private void Kill(int dead)
        {
            dead = Mathf.Min(dead, count);
            count -= dead;
            TotalDied += dead;
        }

        private void HypoxiaKill()
        {
            int dead = Mathf.Max(1, Mathf.RoundToInt(count * Rand.Range(0.2f, 0.5f)));
            Kill(dead);
            Find.LetterStack.ReceiveLetter("DepthRim.NetCage.HypoxiaLetter".Translate(), "DepthRim.NetCage.HypoxiaLetterDesc".Translate(species.LabelCap, dead, count), LetterDefOf.NegativeEvent, parent);
        }

        private void CheckFailed()
        {
            if (HasFish && count < stockedCount * NetCageMath.FailedFraction)
            {
                Messages.Message("DepthRim.NetCage.Failed".Translate(species.LabelCap), parent, MessageTypeDefOf.NegativeEvent);
                ClearFish();
            }
            else if (species != null && count <= 0)
            {
                ClearFish();
            }
        }

        // ── 破網與逃逸（§6.1、§6.6）──

        /// <summary>海況傷害結算後呼叫（外海）。</summary>
        public void Notify_StormHit()
        {
            float chance = NetCageMath.StormBreachChance(Integrity);
            if (HasFish && (debugAlwaysBreach || (chance > 0f && Rand.Chance(chance))))
            {
                Breach(Rand.Range(NetCageMath.BreachLossRange.x, NetCageMath.BreachLossRange.y));
            }
        }

        /// <summary>破網：損失一部分魚（乘品種的逃逸傾向），生成逃逸魚。</summary>
        public int Breach(float lossFraction)
        {
            if (!HasFish) { return 0; }
            int lost = Mathf.Clamp(Mathf.RoundToInt(count * Mathf.Clamp01(lossFraction * species.escapeRisk)), 1, count);
            Escape(lost);
            int now = Find.TickManager.TicksGame;
            if (now - lastBreachMessageTick >= GenDate.TicksPerDay / 2)
            {
                lastBreachMessageTick = now;
                Messages.Message("DepthRim.NetCage.Breach".Translate(lost), parent, MessageTypeDefOf.NegativeEvent);
            }
            CheckFailed();
            return lost;
        }

        /// <summary>箱網被摧毀：魚全部逃走。</summary>
        public void EscapeAll()
        {
            if (HasFish)
            {
                Escape(count);
                ClearFish();
            }
        }

        private void Escape(int lost)
        {
            AquacultureSpeciesDef sp = species;
            count -= lost;
            TotalEscaped += lost;
            if (parent.Spawned)
            {
                EscapePawnsSpawned += EscapedFish.Spawn(parent.Map, parent.OccupiedRect(), sp, Mathf.Min(lost, NetCageMath.MaxEscapePawns));
            }
        }

        // ── 介面 ──

        public override string CompInspectStringExtra()
        {
            StringBuilder sb = new StringBuilder();
            if (!MapComponent_Aquaculture.Enabled)
            {
                sb.AppendLine("DepthRim.NetCage.Disabled".Translate());
            }
            string depth = "DepthRim.NetCage.LayerLine".Translate(("DepthRim.NetLayer." + Props.layer).Translate(), Props.netDepth.ToString("0"), waterTemp.ToString("0.0"), Integrity.ToStringPercent());
            if (HasFish)
            {
                float daysLeft = DaysToHarvest();
                sb.AppendLine("DepthRim.NetCage.FishLine".Translate(species.LabelCap, count, avgWeight.ToString("0.00"), WeightRatio.ToStringPercent()));
                sb.AppendLine("DepthRim.NetCage.BiomassLine".Translate(Biomass.ToString("0"), Props.capacityKg.ToString("0"),
                    Ready ? "DepthRim.NetCage.ReadyNow".Translate().ToString() : daysLeft < 0f ? "DepthRim.NetCage.NotGrowing".Translate().ToString() : "DepthRim.NetCage.ReadyIn".Translate(daysLeft.ToString("0.#")).ToString()));
                float daily = NetCageMath.DailyFeed(Biomass, species.feedRate, feedRateSetting);
                string lasts = daily > 0f ? (feedStore / daily).ToString("0.#") : "-";
                sb.AppendLine("DepthRim.NetCage.FeedLine".Translate(feedStore.ToString("0"), Props.feedCapacity.ToString("0"), lasts, feedRateSetting.ToStringPercent()));
                sb.AppendLine("DepthRim.NetCage.OxygenLine".Translate(oxygen.ToStringPercent()) + " · " + depth);
                List<Building_NetCage> near = new List<Building_NetCage>();
                MapComp?.CentersWithin(Cage, DiseaseSpread.NeighborRadius, near);
                sb.Append("DepthRim.NetCage.DiseaseLine".Translate(diseaseSeverity.ToStringPercent(), near.Count));
            }
            else
            {
                sb.AppendLine("DepthRim.NetCage.Empty".Translate());
                if (pendingSpecies != null)
                {
                    sb.AppendLine("DepthRim.NetCage.PendingStock".Translate(pendingSpecies.LabelCap, pendingCount));
                }
                sb.AppendLine("DepthRim.NetCage.OxygenLine".Translate(oxygen.ToStringPercent()));
                sb.Append(depth);
            }
            if (replaceNetOrdered)
            {
                sb.AppendLine();
                sb.Append("DepthRim.NetCage.ReplaceOrdered".Translate(clothStored, ReplaceCloth));
            }
            return sb.ToString().TrimEndNewlines();
        }

        /// <summary>照目前的條件，還要幾天到上市體重；不長了回傳 −1。</summary>
        public float DaysToHarvest()
        {
            if (!HasFish) { return -1f; }
            float target = species.harvestWeight * NetCageMath.HarvestReady;
            if (avgWeight >= target) { return 0f; }
            float g = NetCageMath.GrowthPerDay(species.harvestWeight, species.growDays, species.TemperatureDistance(waterTemp), oxygen, SustainedLowOxygen, Density, feedStore > 0f, env.growthFactor * feedRateSetting);
            return g <= 0f ? -1f : (target - avgWeight) / g;
        }

        private static readonly float[] FeedRates = { 1f, 0.7f, 0.5f, 0f };

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer)
            {
                yield break;
            }
            if (!HasFish)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DepthRim.NetCage.Stock".Translate(),
                    defaultDesc = "DepthRim.NetCage.StockDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("Things/Item/Resource/AnimalProductRaw/Fish/Fish_Cod", false) ?? BaseContent.BadTex,
                    action = OpenStockMenu
                };
                if (pendingSpecies != null)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DepthRim.NetCage.CancelStock".Translate(),
                        icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel"),
                        action = CancelStock
                    };
                }
            }
            else
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "DepthRim.NetCage.Harvest".Translate(),
                    defaultDesc = "DepthRim.NetCage.HarvestDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Harvest"),
                    isActive = () => harvestOrdered,
                    toggleAction = () => harvestOrdered = !harvestOrdered
                };
                Command_Toggle emergency = new Command_Toggle
                {
                    defaultLabel = "DepthRim.NetCage.EmergencyHarvest".Translate(),
                    defaultDesc = "DepthRim.NetCage.EmergencyHarvestDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Harvest"),
                    defaultIconColor = new Color(1f, 0.5f, 0.4f),
                    isActive = () => emergencyHarvest,
                    toggleAction = () => emergencyHarvest = !emergencyHarvest
                };
                if (WeightRatio < NetCageMath.EmergencyMin)
                {
                    emergency.Disable("DepthRim.NetCage.TooSmall".Translate(NetCageMath.EmergencyMin.ToStringPercent()));
                }
                yield return emergency;
                yield return new Command_Action
                {
                    defaultLabel = "DepthRim.NetCage.FeedRate".Translate(feedRateSetting.ToStringPercent()),
                    defaultDesc = "DepthRim.NetCage.FeedRateDesc".Translate(),
                    icon = ThingDefOf.Kibble.uiIcon,
                    action = () =>
                    {
                        int i = System.Array.IndexOf(FeedRates, feedRateSetting);
                        feedRateSetting = FeedRates[(i + 1 + FeedRates.Length) % FeedRates.Length];
                    }
                };
            }
            Command_Toggle replace = new Command_Toggle
            {
                defaultLabel = "DepthRim.NetCage.ReplaceNet".Translate(),
                defaultDesc = "DepthRim.NetCage.ReplaceNetDesc".Translate(ReplaceCloth),
                icon = ThingDefOf.Cloth.uiIcon,
                isActive = () => replaceNetOrdered,
                toggleAction = () => replaceNetOrdered = !replaceNetOrdered
            };
            if (!CanReplaceNet && !replaceNetOrdered)
            {
                replace.Disable("DepthRim.NetCage.NetFine".Translate());
            }
            yield return replace;

            if (DebugSettings.ShowDevGizmos)
            {
                yield return new Command_Action { defaultLabel = "DEV: grow 1 day", action = () => { for (int i = 0; i < GenDate.TicksPerDay / TickInterval; i++) { Advance(TickInterval); } } };
                yield return new Command_Action { defaultLabel = "DEV: fill feed", action = () => AddFeed(Props.feedCapacity) };
                yield return new Command_Action { defaultLabel = "DEV: ready", action = () => { if (HasFish) { avgWeight = species.harvestWeight; } } };
                yield return new Command_Action { defaultLabel = "DEV: breach", action = () => Breach(0.1f) };
                yield return new Command_Action { defaultLabel = "DEV: infect", action = Infect };
            }
        }

        /// <summary>
        /// 放養只選品種（2026-11-21 使用者決定）：尾數固定是品種的 stockCount，不讓玩家調。
        /// 魚自然長大，長到超過容量就由密度造成的損耗（過密死亡、成長變慢）自己淘汰。
        /// </summary>
        private void OpenStockMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (AquacultureSpeciesDef def in DefDatabase<AquacultureSpeciesDef>.AllDefsListForReading)
            {
                string reason = CannotStockReason(def);
                string label = "DepthRim.NetCage.StockOption".Translate(def.LabelCap, def.stockCount, def.Temperature.min.ToString("0"), def.Temperature.max.ToString("0"), def.growDays.ToString("0"));
                if (reason != null)
                {
                    options.Add(new FloatMenuOption(label + ": " + reason, null));
                    continue;
                }
                AquacultureSpeciesDef local = def;
                options.Add(new FloatMenuOption(label, () => OrderStock(local, local.stockCount)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
