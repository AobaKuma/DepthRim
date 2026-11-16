using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DepthRim
{
    /// <summary>
    /// 水下 AI：殖民者缺氧或睡覺處淹水時自己找安全處（對應原版 Odyssey 真空的 <c>JobGiver_FindOxygen</c>）。
    /// <list type="bullet">
    /// <item>醒著：<see cref="JobGiver_SeekAir"/> 掛在 <c>HumanlikeConstant</c> 思考樹（見 Patches/Flooding_SeekAir.xml），
    /// 沒入水中且沒有潛水裝備、或氣量低於 <see cref="FloodingMath.SeekAirBelowPercent"/> 時游到乾處。</item>
    /// <item>睡著：常駐思考樹只對醒著的人運作，所以由 <see cref="FloodingPawnEffects"/> 每次水位更新檢查，
    /// 床（或地鋪）淹水、或靠氣瓶睡在水裡但氣快用完時直接叫醒並派這個工作。</item>
    /// <item>找床與找地鋪時排除淹水的位置（<see cref="Patch_IsValidBedFor"/>、<see cref="Patch_GroundSleepCell"/>），避免醒來又躺回去。</item>
    /// </list>
    /// 只管玩家的人（殖民者、奴隸、囚犯）；徵召中的人由玩家控制。倒地的人自己動不了，不在這裡處理。
    /// </summary>
    public static class SeekAirUtility
    {
        /// <summary>找不到安全處時，這段時間內不再對同一個人重找（開放海域找不到時整張地圖的區域都會被走一遍）。</summary>
        public const int FailedSearchCooldownTicks = 250;

        /// <summary>最多走訪的區域數。</summary>
        private const int MaxRegions = 2000;

        /// <summary>玩家強制下達的工作，嗆水到這個嚴重度（「窒息」階段）才打斷。</summary>
        private const float ForcedJobDrowningLimit = 0.3f;

        private static readonly Dictionary<int, int> failedSearchTick = new Dictionary<int, int>();

        private static readonly List<IntVec3> tmpCells = new List<IntVec3>();

        public static bool Applies(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || !pawn.RaceProps.Humanlike || !pawn.RaceProps.IsFlesh)
            {
                return false;
            }
            if (pawn.Drafted)
            {
                return false;
            }
            return pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony;
        }

        /// <summary>這個人現在要不要去找空氣（醒著的判斷，用目前的姿勢）。</summary>
        public static bool InDanger(Pawn pawn, MapComponent_Flooding flooding)
        {
            float level = flooding.LevelAt(pawn.Position);
            CompDivingAir air = CompDivingAir.WornBy(pawn);
            return FloodingMath.NeedsToSeekAir(pawn.RaceProps.IsFlesh, FloodingPawnEffects.IsSubmerged(pawn, level), air != null, air?.AirPercent ?? 0f);
        }

        /// <summary>睡著的人要不要叫醒：睡覺處淹水，或在水中靠氣瓶撐著但氣量偏低。</summary>
        public static bool SleeperInDanger(Pawn pawn, MapComponent_Flooding flooding)
        {
            float level = flooding.LevelAt(pawn.Position);
            CompDivingAir air = CompDivingAir.WornBy(pawn);
            return FloodingMath.SleeperMustEvacuate(pawn.RaceProps.IsFlesh, level >= FloodingPawnEffects.SubmergedLevelLying, air != null, air?.AirPercent ?? 0f,
                FloodingPawnEffects.CanBreatheUnderwater(pawn), level);
        }

        /// <summary>這張床（任何一格）是否淹到不能讓 <paramref name="sleeper"/> 睡。</summary>
        public static bool BedFloodedFor(Building_Bed bed, Pawn sleeper)
        {
            Map map = bed.Map;
            if (map == null || sleeper == null || !sleeper.RaceProps.IsFlesh)
            {
                return false;
            }
            MapComponent_Flooding flooding = map.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return false;
            }
            float level = 0f;
            foreach (IntVec3 c in bed.OccupiedRect())
            {
                float l = flooding.LevelAt(c);
                if (l > level)
                {
                    level = l;
                }
            }
            return FloodingMath.SleepSpotFlooded(true, FloodingPawnEffects.CanBreatheUnderwater(sleeper), level);
        }

        public static bool CellFloodedForSleep(IntVec3 c, Pawn sleeper)
        {
            MapComponent_Flooding flooding = sleeper.Map?.Flooding();
            if (flooding == null || !flooding.Active || !sleeper.RaceProps.IsFlesh)
            {
                return false;
            }
            return FloodingMath.SleepSpotFlooded(true, FloodingPawnEffects.CanBreatheUnderwater(sleeper), flooding.LevelAt(c));
        }

        /// <summary>
        /// 做一個找空氣的工作（找不到安全處時回傳 null）。
        /// 先找水位低於 <see cref="FloodingMath.SleepSafeLevel"/> 的地方（躺下、倒地都安全），沒有的話退而求其次找站著不會被淹沒的地方。
        /// </summary>
        public static Job TryMakeJob(Pawn pawn, MapComponent_Flooding flooding)
        {
            int now = Find.TickManager.TicksGame;
            if (failedSearchTick.TryGetValue(pawn.thingIDNumber, out int failed) && now - failed < FailedSearchCooldownTicks && now >= failed)
            {
                return null;
            }
            IntVec3 cell = FindSafeCell(pawn, flooding);
            if (!cell.IsValid)
            {
                failedSearchTick[pawn.thingIDNumber] = now;
                return null;
            }
            failedSearchTick.Remove(pawn.thingIDNumber);
            Job job = JobMaker.MakeJob(DepthRimDefOf.DepthRim_SeekAir, cell);
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            return job;
        }

        /// <summary>玩家強制的工作在嗆水到「窒息」之前不打斷。</summary>
        public static bool RespectForcedJob(Pawn pawn)
        {
            if (!PawnUtility.PlayerForcedJobNowOrSoon(pawn))
            {
                return false;
            }
            Hediff drowning = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Drowning);
            return drowning == null || drowning.Severity < ForcedJobDrowningLimit;
        }

        /// <summary>
        /// 叫醒睡在淹水處的人並派工作（由 <see cref="FloodingPawnEffects"/> 每次水位更新呼叫）。
        /// 回傳是否派了工作。
        /// </summary>
        public static bool TryEvacuateSleeper(Pawn pawn, MapComponent_Flooding flooding)
        {
            if (pawn.Awake() || pawn.Downed || pawn.InMentalState || !Applies(pawn) || pawn.CurJobDef == DepthRimDefOf.DepthRim_SeekAir)
            {
                return false;
            }
            if (!SleeperInDanger(pawn, flooding))
            {
                return false;
            }
            Job job = TryMakeJob(pawn, flooding);
            if (job == null)
            {
                return false;
            }
            pawn.jobs.StartJob(job, JobCondition.InterruptForced, resumeCurJobAfterwards: false);
            return pawn.CurJobDef == DepthRimDefOf.DepthRim_SeekAir;
        }

        public static IntVec3 FindSafeCell(Pawn pawn, MapComponent_Flooding flooding)
        {
            Map map = pawn.Map;
            Region start = pawn.Position.GetRegion(map);
            if (start == null)
            {
                return IntVec3.Invalid;
            }
            TraverseParms parms = TraverseParms.For(pawn);
            IntVec3 best = IntVec3.Invalid;
            IntVec3 fallback = IntVec3.Invalid;
            RegionTraverser.BreadthFirstTraverse(start, (Region from, Region r) => r.Allows(parms, isDestination: false), delegate (Region r)
            {
                if (r.IsDoorway)
                {
                    return false;
                }
                if (TryGetSafeCellInRegion(r, pawn, flooding, FloodingMath.SleepSafeLevel, out IntVec3 dry))
                {
                    best = dry;
                    return true;
                }
                if (!fallback.IsValid && TryGetSafeCellInRegion(r, pawn, flooding, FloodingMath.SubmergedLevelStanding, out IntVec3 shallow))
                {
                    fallback = shallow;
                }
                return false;
            }, MaxRegions);
            return best.IsValid ? best : fallback;
        }

        /// <summary>區域內最靠近 pawn、水位低於 <paramref name="maxLevel"/> 的可站立格；優先選允許區域內的格子。</summary>
        private static bool TryGetSafeCellInRegion(Region region, Pawn pawn, MapComponent_Flooding flooding, float maxLevel, out IntVec3 cell)
        {
            Map map = pawn.Map;
            cell = IntVec3.Invalid;
            IntVec3 outsideArea = IntVec3.Invalid;
            int bestDist = int.MaxValue;
            int bestOutside = int.MaxValue;
            tmpCells.Clear();
            tmpCells.AddRange(region.Cells);
            for (int i = 0; i < tmpCells.Count; i++)
            {
                IntVec3 c = tmpCells[i];
                if (flooding.LevelAt(c) >= maxLevel || c.Fogged(map) || !c.Standable(map))
                {
                    continue;
                }
                int dist = (c - pawn.Position).LengthHorizontalSquared;
                if (c.InAllowedArea(pawn))
                {
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        cell = c;
                    }
                }
                else if (dist < bestOutside)
                {
                    bestOutside = dist;
                    outsideArea = c;
                }
            }
            tmpCells.Clear();
            if (!cell.IsValid)
            {
                cell = outsideArea;
            }
            return cell.IsValid;
        }
    }

    /// <summary>醒著的人在水中缺氧時去找空氣（掛在 <c>HumanlikeConstant</c>，原版每 30 ticks 判斷一次）。</summary>
    public class JobGiver_SeekAir : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!SeekAirUtility.Applies(pawn) || pawn.CurJobDef == DepthRimDefOf.DepthRim_SeekAir)
            {
                return null;
            }
            MapComponent_Flooding flooding = pawn.Map.Flooding();
            if (flooding == null || !flooding.Active || !SeekAirUtility.InDanger(pawn, flooding))
            {
                return null;
            }
            if (SeekAirUtility.RespectForcedJob(pawn))
            {
                return null;
            }
            return SeekAirUtility.TryMakeJob(pawn, flooding);
        }
    }

    /// <summary>游到安全處；穿著潛水裝備的話在原地等到氣量回滿（<see cref="FloodingMath.SeekAirRefilledPercent"/>）。</summary>
    public class JobDriver_SeekAir : JobDriver
    {
        /// <summary>補氣最多等這麼久（滿瓶 30000 ticks 以每 tick 10 回充，從零補滿約 3000 ticks）。</summary>
        public const int MaxRefillTicks = 4000;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil wait = ToilMaker.MakeToil("SeekAirRefill");
            wait.defaultCompleteMode = ToilCompleteMode.Delay;
            wait.defaultDuration = MaxRefillTicks;
            wait.handlingFacing = true;
            wait.tickIntervalAction = delegate (int delta)
            {
                CompDivingAir air = CompDivingAir.WornBy(pawn);
                if (FloodingMath.DoneRefilling(air != null, air?.AirPercent ?? 1f))
                {
                    ReadyForNextToil();
                    return;
                }
                // 這裡也淹了（水位上升）：結束工作，讓思考樹重新找地方
                MapComponent_Flooding flooding = pawn.Map.Flooding();
                if (flooding != null && SeekAirUtility.InDanger(pawn, flooding))
                {
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            yield return wait;
        }
    }

    /// <summary>淹水的床不算能睡的床（不能在水中呼吸的人）。</summary>
    [HarmonyPatch(typeof(RestUtility), nameof(RestUtility.IsValidBedFor))]
    public static class Patch_IsValidBedFor
    {
        public static void Postfix(Thing bedThing, Pawn sleeper, ref bool __result)
        {
            if (__result && bedThing is Building_Bed bed && SeekAirUtility.BedFloodedFor(bed, sleeper))
            {
                __result = false;
            }
        }
    }

    /// <summary>沒有床時的地鋪也不選淹水的格子。</summary>
    [HarmonyPatch(typeof(JobGiver_GetRest), "IsValidCell")]
    public static class Patch_GroundSleepCell
    {
        public static void Postfix(Pawn pawn, IntVec3 cell, ref bool __result)
        {
            if (__result && SeekAirUtility.CellFloodedForSleep(cell, pawn))
            {
                __result = false;
            }
        }
    }
}
