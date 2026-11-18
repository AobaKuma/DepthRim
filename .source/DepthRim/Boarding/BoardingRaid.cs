using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DepthRim
{
    /// <summary>跳幫襲擊的共用判斷：哪些地圖會被襲擊、艇降在哪、襲擊者的裝備。</summary>
    public static class BoardingUtility
    {
        public static readonly IntVec2 CraftSize = new IntVec2(3, 2);

        /// <summary>水下襲擊的條件（設計稿 §6、§11 4.5）：玩家在海洋層上的地圖，而且開著主動聲納。</summary>
        public static bool RaidableOceanMap(Map map)
        {
            return map?.Parent != null && map.Parent.Faction == Faction.OfPlayer && OceanRouting.IsOcean(map.Tile)
                && SonarUtility.HasActiveSonar(map, Faction.OfPlayer);
        }

        public static Building_BoardingCraft CraftOf(Lord lord)
        {
            if (lord?.Map == null)
            {
                return null;
            }
            foreach (Thing t in lord.Map.listerThings.ThingsOfDef(BoardingDefOf.DepthRim_BoardingCraft))
            {
                if (t is Building_BoardingCraft craft && !craft.Destroyed && craft.raiders.Any(p => lord.ownedPawns.Contains(p)))
                {
                    return craft;
                }
            }
            return null;
        }

        private static bool FootprintClear(Map map, IntVec3 center, Room room)
        {
            CellRect rect = GenAdj.OccupiedRect(center, Rot4.North, CraftSize);
            foreach (IntVec3 c in rect.ExpandedBy(room != null ? 0 : 1))
            {
                if (!c.InBounds(map) || !c.Standable(map) || c.GetEdifice(map) != null || c.GetFirstPawn(map) != null
                    || c.GetFirstThing<Building>(map) != null)
                {
                    return false;
                }
                if (room != null && rect.Contains(c) && c.GetRoom(map) != room)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 降落點：優先選玩家的密閉艙室（有屋頂、不碰地圖邊、裡面有玩家的建築），艇直接穿入；
        /// 沒有放得下的艙室就降在玩家建築附近的開放海床（§6.3：在艙外放下潛水的突擊隊）。
        /// </summary>
        public static bool TryFindLandingCell(Map map, out IntVec3 cell, out bool inside)
            => TryFindLandingCell(map, Faction.OfPlayer, out cell, out inside);

        /// <summary>同上，但切入 <paramref name="target"/> 陣營的艙室（玩家艇跳幫敵方時用）。</summary>
        public static bool TryFindLandingCell(Map map, Faction target, out IntVec3 cell, out bool inside)
        {
            cell = IntVec3.Invalid;
            inside = false;
            List<IntVec3> candidates = new List<IntVec3>();
            int needed = CraftSize.x * CraftSize.z + 4;
            foreach (Room room in map.regionGrid.AllRooms)
            {
                if (room.TouchesMapEdge || room.IsDoorway || room.OpenRoofCount > 0 || room.CellCount < needed)
                {
                    continue;
                }
                if (!room.ContainedAndAdjacentThings.Any(t => t.Faction == target && t is Building))
                {
                    continue;
                }
                foreach (IntVec3 c in room.Cells)
                {
                    if (FootprintClear(map, c, room))
                    {
                        candidates.Add(c);
                    }
                }
            }
            if (candidates.TryRandomElement(out cell))
            {
                inside = true;
                return true;
            }
            List<Building> anchors = target == Faction.OfPlayer ? map.listerBuildings.allBuildingsColonist
                : map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).OfType<Building>().Where(b => b.Faction == target).ToList();
            for (int i = 0; i < 30 && anchors.Count > 0; i++)
            {
                IntVec3 near = anchors.RandomElement().Position;
                if (CellFinder.TryFindRandomCellNear(near, map, 20, c => !c.Roofed(map) && FootprintClear(map, c, null), out cell))
                {
                    return true;
                }
            }
            return CellFinder.TryFindRandomCell(map, c => !c.Roofed(map) && FootprintClear(map, c, null), out cell);
        }

        /// <summary>這一格是不是在密閉艙室裡（有屋頂、不碰地圖邊）：決定突擊艇降落後是直接打，還是先破牆。</summary>
        public static bool IsInsideCompartment(Map map, IntVec3 cell)
        {
            Room room = cell.IsValid && cell.InBounds(map) ? cell.GetRoom(map) : null;
            return room != null && !room.TouchesMapEdge && !room.IsDoorway && room.OpenRoofCount == 0;
        }

        /// <summary>沉沒的落點：附近沒有屋頂、可站的海床格（沉到艙外）；找不到就原地。</summary>
        public static IntVec3 SeabedNear(Map map, IntVec3 near)
        {
            if (CellFinder.TryFindRandomCellNear(near, map, 8, c => c.Standable(map) && !c.Roofed(map), out IntVec3 cell))
            {
                return cell;
            }
            return near.InBounds(map) ? near : map.Center;
        }

        /// <summary>撤退時順手帶走的東西：附近倒地的殖民者（俘虜）優先，其次是值錢的物品。</summary>
        public static Thing FindTakeable(Pawn pawn, float radius)
        {
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return null;
            }
            if (KidnapAIUtility.TryFindGoodKidnapVictim(pawn, radius, out Pawn victim))
            {
                return victim;
            }
            return GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForGroup(ThingRequestGroup.HaulableEver),
                PathEndMode.ClosestTouch, TraverseParms.For(pawn, Danger.Some), radius,
                t => !(t is Pawn) && !(t is Corpse) && !(t is Building) && t.def.EverHaulable
                    && t.MarketValue * t.stackCount >= 200f && pawn.CanReserve(t));
        }

        /// <summary>突擊隊員都穿潛水服（水下襲擊，艙外是開放水域）。</summary>
        public static void GiveDivingGear(Pawn pawn)
        {
            ThingDef suitDef = DefDatabase<ThingDef>.GetNamedSilentFail("DepthRim_DivingSuit");
            if (suitDef == null || pawn.apparel == null || !pawn.RaceProps.Humanlike)
            {
                return;
            }
            // 已經有可用的水下呼吸裝備（潛水服，或配齊的原版真空服）就不換
            if (CompDivingAir.WornBy(pawn) != null)
            {
                return;
            }
            Apparel suit = (Apparel)ThingMaker.MakeThing(suitDef, suitDef.MadeFromStuff ? GenStuff.DefaultStuffFor(suitDef) : null);
            pawn.apparel.Wear(suit, dropReplacedApparel: false);
        }

        /// <summary>適合跳幫的敵方：敵對、有人形、工業以上、組得出戰鬥隊。</summary>
        public static bool TryFindRaidFaction(float points, out Faction faction)
        {
            return Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer && !f.defeated && !f.deactivated && !f.temporary && !f.def.hidden && f.def.humanlikeFaction
                    && f.def.techLevel >= TechLevel.Industrial && f.HostileTo(Faction.OfPlayer)
                    && f.def.pawnGroupMakers != null && f.def.pawnGroupMakers.Any(g => g.kindDef == PawnGroupKindDefOf.Combat)
                    && points >= f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat))
                .TryRandomElement(out faction);
        }
    }

    /// <summary>突擊艇抵達：每艇最多 <see cref="PawnsPerCraft"/> 人，降在 <see cref="BoardingUtility.TryFindLandingCell"/> 選的位置。</summary>
    public class PawnsArrivalModeWorker_BoardingCraft : PawnsArrivalModeWorker
    {
        public const int PawnsPerCraft = 6;

        public override void Arrive(List<Pawn> pawns, IncidentParms parms)
        {
            Map map = (Map)parms.target;
            List<Pawn> list = pawns.ToList();
            IntVec3 cell = parms.spawnCenter;
            for (int start = 0; start < list.Count; start += PawnsPerCraft)
            {
                if (start > 0 && !BoardingUtility.TryFindLandingCell(map, out cell, out _))
                {
                    cell = parms.spawnCenter;
                }
                Building_BoardingCraft craft = (Building_BoardingCraft)ThingMaker.MakeThing(BoardingDefOf.DepthRim_BoardingCraft);
                craft.SetFaction(parms.faction);
                foreach (Pawn pawn in list.Skip(start).Take(PawnsPerCraft))
                {
                    MarineXenotypes.TryAssignCrew(pawn);
                    BoardingUtility.GiveDivingGear(pawn);
                    craft.Load(pawn);
                }
                // 經 SkyfallerMaker：DMSE 的地圖層攔截會接手（有聲納時可能在半路被擊落）
                SkyfallerMaker.SpawnSkyfaller(BoardingDefOf.DepthRim_BoardingCraftIncoming, craft, cell, map);
            }
        }

        public override bool TryResolveRaidSpawnCenter(IncidentParms parms)
        {
            Map map = (Map)parms.target;
            parms.spawnRotation = Rot4.North;
            if (parms.spawnCenter.IsValid)
            {
                return true;
            }
            return BoardingUtility.TryFindLandingCell(map, out parms.spawnCenter, out _);
        }
    }

    public class RaidStrategyWorker_Boarding : RaidStrategyWorker
    {
        protected override LordJob MakeLordJob(IncidentParms parms, Map map, List<Pawn> pawns, int raidSeed)
        {
            return new LordJob_BoardingRaid(parms.faction, breach: !BoardingUtility.IsInsideCompartment(map, parms.spawnCenter));
        }
    }

    /// <summary>跳幫襲擊：先突擊，撐不住、打夠了或時間到就回艇撤退（不走地圖邊，水下沒有路可以走）。</summary>
    public class LordJob_BoardingRaid : LordJob
    {
        private Faction faction;

        /// <summary>降在艙外：先用原版的破牆突擊打穿外殼（破口會進水），再照常突擊。</summary>
        private bool breach;

        public LordJob_BoardingRaid()
        {
        }

        public LordJob_BoardingRaid(Faction faction, bool breach = false)
        {
            this.faction = faction;
            this.breach = breach;
        }

        public bool Breaching => breach;

        public override bool AddFleeToil => false;

        public override bool GuiltyOnDowned => true;

        public override StateGraph CreateGraph()
        {
            StateGraph graph = new StateGraph();
            List<LordToil> fighting = new List<LordToil>();
            if (breach)
            {
                LordToil_AssaultColonyBreaching breaching = new LordToil_AssaultColonyBreaching();
                graph.AddToil(breaching);
                fighting.Add(breaching);
            }
            LordToil_AssaultColony assault = new LordToil_AssaultColony();
            graph.AddToil(assault);
            LordToil_ReturnToCraft ret = new LordToil_ReturnToCraft();
            graph.AddToil(ret);
            string plural = faction?.def.pawnsPlural.CapitalizeFirst() ?? "";
            string name = faction?.Name ?? "";

            Transition lost = new Transition(assault, ret);
            lost.AddSources(fighting);
            lost.AddTrigger(new Trigger_FractionPawnsLost(0.5f));
            lost.AddPreAction(new TransitionAction_Message("DepthRim.Boarding.RetreatLosses".Translate(plural, name)));
            graph.AddTransition(lost);

            Transition timeout = new Transition(assault, ret);
            timeout.AddSources(fighting);
            timeout.AddTrigger(new Trigger_TicksPassed(new IntRange(26000, 38000).RandomInRange));
            timeout.AddPreAction(new TransitionAction_Message("DepthRim.Boarding.RetreatTimeout".Translate(plural, name)));
            graph.AddTransition(timeout);

            Transition damage = new Transition(assault, ret);
            damage.AddSources(fighting);
            damage.AddTrigger(new Trigger_FractionColonyDamageTaken(new FloatRange(0.25f, 0.35f).RandomInRange, 900f));
            damage.AddPreAction(new TransitionAction_Message("DepthRim.Boarding.RetreatSatisfied".Translate(plural, name)));
            graph.AddTransition(damage);

            Transition peace = new Transition(assault, ret);
            peace.AddSources(fighting);
            peace.AddTrigger(new Trigger_BecameNonHostileToPlayer());
            graph.AddTransition(peace);

            // 開發／測試：送 "DepthRim.BoardingRetreat" 訊號就撤退
            Transition signal = new Transition(assault, ret);
            signal.AddSources(fighting);
            signal.AddTrigger(new Trigger_Signal("DepthRim.BoardingRetreat"));
            graph.AddTransition(signal);
            return graph;
        }

        public override void ExposeData()
        {
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref breach, "breach", false);
        }
    }

    /// <summary>回艇：每人的 duty 指向自己這群的艇；艇沒了就只能繼續打。</summary>
    public class LordToil_ReturnToCraft : LordToil
    {
        public override bool AllowSatisfyLongNeeds => false;

        public override void UpdateAllDuties()
        {
            Building_BoardingCraft craft = BoardingUtility.CraftOf(lord);
            craft?.RequestReturn();
            foreach (Pawn pawn in lord.ownedPawns)
            {
                pawn.mindState.duty = craft != null
                    ? new PawnDuty(BoardingDefOf.DepthRim_ReturnToCraft, craft) { locomotion = LocomotionUrgency.Jog }
                    : new PawnDuty(DutyDefOf.AssaultColony);
            }
        }

        public override void LordToilTick()
        {
            if (Find.TickManager.TicksGame % 250 == 0)
            {
                Building_BoardingCraft craft = BoardingUtility.CraftOf(lord);
                bool hasCraft = craft != null;
                bool dutiesPointAtCraft = lord.ownedPawns.All(p => p.mindState.duty?.def == BoardingDefOf.DepthRim_ReturnToCraft);
                if (hasCraft != dutiesPointAtCraft)
                {
                    UpdateAllDuties();
                }
            }
        }
    }

    /// <summary>撤退時，沒扛東西的人先扛起附近倒地的殖民者或值錢的東西，再上艇。</summary>
    public class JobGiver_CarryToCraft : ThinkNode_JobGiver
    {
        public float radius = 12f;

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!(pawn.mindState.duty?.focus.Thing is Building_BoardingCraft craft) || !craft.Spawned || craft.Map != pawn.Map
                || pawn.carryTracker.CarriedThing != null)
            {
                return null;
            }
            Thing take = BoardingUtility.FindTakeable(pawn, radius);
            if (take == null || !pawn.CanReach(craft, PathEndMode.Touch, Danger.Deadly))
            {
                return null;
            }
            Job job = JobMaker.MakeJob(BoardingDefOf.DepthRim_CarryToCraft, take, craft);
            job.count = take.stackCount;
            job.locomotionUrgency = LocomotionUrgency.Jog;
            return job;
        }
    }

    public class JobDriver_CarryToCraft : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnDespawnedOrNull(TargetIndex.B);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnSomeonePhysicallyInteracting(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
            Toil enter = ToilMaker.MakeToil("CarryToCraft");
            enter.initAction = delegate
            {
                if (job.targetB.Thing is Building_BoardingCraft craft && craft.Spawned)
                {
                    craft.Board(pawn);
                }
            };
            enter.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return enter;
        }
    }

    /// <summary>玩家：右鍵自己的突擊艇「登艇」。</summary>
    public class FloatMenuOptionProvider_BoardCraft : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => true;

        protected override FloatMenuOption GetSingleOptionFor(Thing clickedThing, FloatMenuContext context)
        {
            if (!(clickedThing is Building_BoardingCraft craft) || craft.Faction != Faction.OfPlayer)
            {
                return null;
            }
            List<Pawn> pawns = context.ValidSelectedPawns.Where(p => p.CanReach(craft, PathEndMode.Touch, Danger.Deadly)).ToList();
            if (pawns.Count == 0)
            {
                return new FloatMenuOption("DepthRim.Boarding.CannotBoard".Translate() + ": " + "NoPath".Translate(), null);
            }
            return new FloatMenuOption("DepthRim.Boarding.BoardOption".Translate(craft.LabelShort), delegate
            {
                foreach (Pawn p in pawns)
                {
                    Job job = JobMaker.MakeJob(BoardingDefOf.DepthRim_BoardCraft, craft);
                    job.playerForced = true;
                    p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }
            }, MenuOptionPriority.High);
        }
    }

    /// <summary>玩家的突擊艇只能造在月池上（整個佔地都在月池裡）。</summary>
    public class PlaceWorker_OnMoonPool : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, checkingDef.Size))
            {
                if (!c.InBounds(map) || !c.GetThingList(map).Any(t => t.def == BoardingDefOf.DepthRim_MoonPool
                    || (t.def.entityDefToBuild == BoardingDefOf.DepthRim_MoonPool)))
                {
                    return "DepthRim.Boarding.NeedsMoonPool".Translate();
                }
            }
            return true;
        }
    }

    public class JobGiver_BoardCraft : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!(pawn.mindState.duty?.focus.Thing is Building_BoardingCraft craft) || !craft.Spawned || craft.Map != pawn.Map)
            {
                return null;
            }
            if (!pawn.CanReach(craft, PathEndMode.Touch, Danger.Deadly))
            {
                return null;
            }
            Job job = JobMaker.MakeJob(BoardingDefOf.DepthRim_BoardCraft, craft);
            job.locomotionUrgency = LocomotionUrgency.Jog;
            return job;
        }
    }

    public class JobDriver_BoardCraft : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil enter = ToilMaker.MakeToil("BoardCraft");
            enter.initAction = delegate
            {
                if (job.targetA.Thing is Building_BoardingCraft craft && craft.Spawned)
                {
                    craft.Board(pawn);
                }
            };
            enter.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return enter;
        }
    }

    /// <summary>
    /// 跳幫襲擊事件：玩家開著主動聲納的海洋地圖。襲擊方式固定為突擊艇＋回艇撤退，敵方陣營自己挑
    /// （原版的陣營篩選會看陣營的抵達圖層白名單，海洋層一般不在裡面）。
    /// </summary>
    public class IncidentWorker_BoardingRaid : IncidentWorker_RaidEnemy
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            if (!(parms.target is Map map) || !BoardingUtility.RaidableOceanMap(map))
            {
                return false;
            }
            float points = parms.points > 0f ? parms.points : StorytellerUtility.DefaultThreatPointsNow(map);
            return BoardingUtility.TryFindRaidFaction(points, out _) && BoardingUtility.TryFindLandingCell(map, out _, out _);
        }

        protected override bool TryResolveRaidFaction(IncidentParms parms)
        {
            if (parms.faction != null && parms.faction.HostileTo(Faction.OfPlayer))
            {
                return true;
            }
            return BoardingUtility.TryFindRaidFaction(parms.points, out parms.faction);
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            parms.raidStrategy = BoardingDefOf.DepthRim_Boarding;
            parms.raidArrivalMode = BoardingDefOf.DepthRim_BoardingArrival;
            if (parms.points <= 0f)
            {
                parms.points = StorytellerUtility.DefaultThreatPointsNow(parms.target);
            }
            return base.TryExecuteWorker(parms);
        }
    }

    public class StorytellerCompProperties_BoardingRaids : StorytellerCompProperties
    {
        public IncidentDef incident;

        /// <summary>每張符合條件的地圖平均幾天一次。</summary>
        public float mtbDays = 12f;

        public StorytellerCompProperties_BoardingRaids()
        {
            compClass = typeof(StorytellerComp_BoardingRaids);
        }
    }

    /// <summary>海洋層地圖不吃原版說書人的襲擊（圖層白名單），跳幫襲擊由這個說書人元件另外排。</summary>
    public class StorytellerComp_BoardingRaids : StorytellerComp
    {
        private StorytellerCompProperties_BoardingRaids Props => (StorytellerCompProperties_BoardingRaids)props;

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (Props.incident == null || GenDate.DaysPassedSinceSettleFloat < props.minDaysPassed
                || !(target is Map map) || !BoardingUtility.RaidableOceanMap(map))
            {
                yield break;
            }
            if (!Rand.MTBEventOccurs(Props.mtbDays, GenDate.TicksPerDay, 1000f))
            {
                yield break;
            }
            IncidentParms parms = GenerateParms(IncidentCategoryDefOf.ThreatBig, target);
            if (Props.incident.Worker.CanFireNow(parms))
            {
                yield return new FiringIncident(Props.incident, this, parms);
            }
        }
    }
}
