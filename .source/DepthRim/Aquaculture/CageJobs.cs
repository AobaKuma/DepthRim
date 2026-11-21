using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DepthRim
{
    // ═══ 工作（§8.1）：放養／收成／補飼料掛馴獸，修網掛建造，治療掛醫療；不新增工作類型 ═══

    /// <summary>把物料（Kibble、布、鋼、藥品）搬進箱網。TargetA = 箱網，TargetB = 物料。</summary>
    public class JobDriver_DeliverToCage : JobDriver
    {
        private Building_NetCage Cage => (Building_NetCage)job.GetTarget(TargetIndex.A).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed)
                   && pawn.Reserve(job.GetTarget(TargetIndex.B), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => pawn.carryTracker.CarriedThing == null && (job.GetTarget(TargetIndex.B).Thing is not Thing b || Cage.Cage.WantedCount(b.def) <= 0));
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, subtractNumTakenFromJobCount: true);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil deposit = ToilMaker.MakeToil("DepositIntoCage");
            deposit.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried != null)
                {
                    Cage.Cage.Deposit(carried);
                    if (!carried.Destroyed && carried.stackCount > 0)
                    {
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    }
                }
            };
            deposit.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return deposit;
        }
    }

    /// <summary>站在箱網旁邊做一段時間的工作。速度看技能（技能 10 = 100%）。</summary>
    public abstract class JobDriver_CageWork : JobDriver
    {
        protected Building_NetCage Cage => (Building_NetCage)job.GetTarget(TargetIndex.A).Thing;

        protected CompNetCage Comp => Cage.Cage;

        protected abstract int WorkTicks { get; }

        protected abstract SkillDef Skill { get; }

        protected abstract bool StillValid();

        protected abstract void Finish();

        public static float SpeedFor(Pawn pawn, SkillDef skill)
        {
            int level = skill != null && pawn.skills != null ? pawn.skills.GetSkill(skill).Level : 10;
            return Mathf.Clamp(0.4f + 0.06f * level, 0.3f, 1.6f);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => !StillValid());
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            float total = WorkTicks;
            float done = 0f;
            Toil work = ToilMaker.MakeToil("CageWork");
            work.tickIntervalAction = delta =>
            {
                done += SpeedFor(pawn, Skill) * delta;
                if (Skill != null) { pawn.skills?.Learn(Skill, 0.08f * delta); }
                if (done >= total) { ReadyForNextToil(); }
            };
            work.defaultCompleteMode = ToilCompleteMode.Never;
            work.WithProgressBar(TargetIndex.A, () => done / total);
            work.FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
            work.handlingFacing = true;
            work.tickAction = () => pawn.rotationTracker.FaceTarget(Cage);
            yield return work;
            Toil finish = ToilMaker.MakeToil("CageWorkFinish");
            finish.initAction = Finish;
            finish.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finish;
        }
    }

    public class JobDriver_StockCage : JobDriver_CageWork
    {
        public const int Ticks = 1200;
        protected override int WorkTicks => Ticks;
        protected override SkillDef Skill => SkillDefOf.Animals;
        protected override bool StillValid() => !Comp.HasFish && Comp.PendingSpecies != null;
        protected override void Finish() => Comp.Stock(Comp.PendingSpecies, Comp.PendingCount);
    }

    public class JobDriver_HarvestCage : JobDriver_CageWork
    {
        public const int Ticks = 1500;
        protected override int WorkTicks => Ticks;
        protected override SkillDef Skill => SkillDefOf.Animals;
        protected override bool StillValid() => Comp.WantsHarvest;
        protected override void Finish() => Comp.Harvest(pawn.Position);
    }

    public class JobDriver_RepairCageNet : JobDriver_CageWork
    {
        protected override int WorkTicks => Comp.ReplaceNetOrdered ? 1200 : 800;
        protected override SkillDef Skill => SkillDefOf.Construction;
        protected override bool StillValid() => Comp.CanDoReplaceWork || Comp.CanDoRepairWork;
        protected override void Finish()
        {
            if (Comp.CanDoReplaceWork) { Comp.FinishReplaceNet(); }
            else if (Comp.CanDoRepairWork) { Comp.FinishRepair(); }
        }
    }

    public class JobDriver_TreatCage : JobDriver_CageWork
    {
        public const int Ticks = 1200;
        protected override int WorkTicks => Ticks;
        protected override SkillDef Skill => SkillDefOf.Medicine;
        protected override bool StillValid() => Comp.CanDoTreatWork;
        protected override void Finish() => Comp.FinishTreatment();
    }

    // ── WorkGivers ──

    public abstract class WorkGiver_Cage : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            return MapComponent_Aquaculture.For(pawn.Map)?.Cages ?? (IEnumerable<Thing>)new List<Thing>();
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            MapComponent_Aquaculture comp = MapComponent_Aquaculture.For(pawn.Map);
            return comp == null || comp.Cages.Count == 0 || !MapComponent_Aquaculture.Enabled;
        }

        protected static bool Usable(Pawn pawn, Thing t, bool forced)
        {
            return t is Building_NetCage && t.Faction == pawn.Faction && !t.IsForbidden(pawn) && !t.IsBurning()
                   && pawn.CanReserveAndReach(t, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced);
        }

        /// <summary>找一堆這座箱網要的物料，做成搬運工作；沒有就回傳 null。</summary>
        protected static Job DeliverJob(Pawn pawn, Building_NetCage cage, ThingRequest request, System.Predicate<Thing> extra = null, JobDef jobDef = null)
        {
            Thing stack = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, request, PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
                t => !t.IsForbidden(pawn) && pawn.CanReserve(t) && cage.Cage.WantedCount(t.def) > 0 && (extra == null || extra(t)));
            if (stack == null)
            {
                return null;
            }
            Job job = JobMaker.MakeJob(jobDef ?? DepthRimDefOf.DepthRim_DeliverToCage, cage, stack);
            job.count = Mathf.Min(stack.stackCount, cage.Cage.WantedCount(stack.def));
            return job;
        }
    }

    public class WorkGiver_StockCage : WorkGiver_Cage
    {
        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return Usable(pawn, t, forced) && t is Building_NetCage c && !c.Cage.HasFish && c.Cage.PendingSpecies != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return JobMaker.MakeJob(DepthRimDefOf.DepthRim_StockCage, t);
        }
    }

    public class WorkGiver_HarvestCage : WorkGiver_Cage
    {
        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return Usable(pawn, t, forced) && t is Building_NetCage c && c.Cage.WantsHarvest;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return JobMaker.MakeJob(DepthRimDefOf.DepthRim_HarvestCage, t);
        }
    }

    public class WorkGiver_RefillCageFeed : WorkGiver_Cage
    {
        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return Usable(pawn, t, forced) && t is Building_NetCage c && (c.Cage.NeedsFeed || (forced && c.Cage.HasFish && c.Cage.FeedSpace >= 1f))
                   && JobOnThing(pawn, t, forced) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return DeliverJob(pawn, (Building_NetCage)t, ThingRequest.ForDef(ThingDefOf.Kibble), null, DepthRimDefOf.DepthRim_RefillCageFeed);
        }
    }

    public class WorkGiver_RepairCageNet : WorkGiver_Cage
    {
        private static bool Wants(CompNetCage c) => c.NeedsRepair || c.ReplaceNetOrdered;

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return Usable(pawn, t, forced) && t is Building_NetCage c && Wants(c.Cage) && JobOnThing(pawn, t, forced) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Building_NetCage cage = (Building_NetCage)t;
            if (cage.Cage.CanDoReplaceWork || cage.Cage.CanDoRepairWork)
            {
                return JobMaker.MakeJob(DepthRimDefOf.DepthRim_RepairCageNet, t);
            }
            if (cage.Cage.ClothNeeded > 0)
            {
                Job j = DeliverJob(pawn, cage, ThingRequest.ForDef(ThingDefOf.Cloth));
                if (j != null) { return j; }
            }
            if (cage.Cage.SteelNeeded > 0)
            {
                return DeliverJob(pawn, cage, ThingRequest.ForDef(ThingDefOf.Steel));
            }
            return null;
        }
    }

    public class WorkGiver_TreatCage : WorkGiver_Cage
    {
        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return Usable(pawn, t, forced) && t is Building_NetCage c && c.Cage.NeedsTreatment && JobOnThing(pawn, t, forced) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Building_NetCage cage = (Building_NetCage)t;
            if (cage.Cage.CanDoTreatWork)
            {
                return JobMaker.MakeJob(DepthRimDefOf.DepthRim_TreatCage, t);
            }
            // 先找一般藥品，沒有才用草藥
            return DeliverJob(pawn, cage, ThingRequest.ForGroup(ThingRequestGroup.Medicine), m => m.def != ThingDefOf.MedicineHerbal)
                   ?? DeliverJob(pawn, cage, ThingRequest.ForDef(ThingDefOf.MedicineHerbal));
        }
    }
}
