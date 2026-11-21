using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DepthRim
{
    // ═══ 藻華（§6.3）與冷暖洋流（§5.5）：地圖級 GameCondition，箱網與洋流發電機只讀它們在不在 ═══

    /// <summary>藻華：所有箱網溶氧 −0.18，3～7 天；天氣轉涼就提早結束。水面每隔一段時間冒綠色水花。</summary>
    public class GameCondition_AlgalBloom : GameCondition
    {
        public override void GameConditionTick()
        {
            base.GameConditionTick();
            Map map = SingleMap;
            if (map == null || !map.IsHashIntervalTick(2500))
            {
                return;
            }
            if (map.mapTemperature.OutdoorTemp < 12f && TicksPassed > GenDate.TicksPerDay)
            {
                End();
                return;
            }
            MapComponent_Aquaculture comp = MapComponent_Aquaculture.For(map);
            if (comp == null || map != Find.CurrentMap)
            {
                return;
            }
            foreach (Building_NetCage cage in comp.Cages)
            {
                if (cage.Spawned)
                {
                    FleckMaker.ThrowDustPuffThick(cage.TrueCenter(), map, 2.5f, new Color(0.35f, 0.62f, 0.25f, 0.6f));
                }
            }
        }
    }

    /// <summary>冷暖洋流：只在這張海面地圖；水溫偏移由 <see cref="ClimateMath.CurrentOffset"/> 算，不直接傷害箱網。</summary>
    public class GameCondition_OceanCurrent : GameCondition
    {
    }

    /// <summary>開啟一個地圖級 GameCondition 的事件（藻華、冷暖洋流），信件文字放在 IncidentDef。</summary>
    public class IncidentWorker_CageCondition : IncidentWorker
    {
        public GameConditionDef Condition => def.gameCondition;

        protected override bool CanFireNowSub(IncidentParms parms)
        {
            Map map = parms.target as Map;
            MapComponent_Aquaculture comp = MapComponent_Aquaculture.For(map);
            if (comp == null || !MapComponent_Aquaculture.Enabled || Condition == null || comp.StockedCages() == 0)
            {
                return false;
            }
            if (map.gameConditionManager.ConditionIsActive(Condition))
            {
                return false;
            }
            bool current = Condition == DepthRimDefOf.DepthRim_WarmCurrent || Condition == DepthRimDefOf.DepthRim_ColdCurrent;
            if (current)
            {
                // 洋流只在外海，而且同時只有一種
                return comp.Venue == CageVenue.OpenSea && comp.Current == OceanCurrentKind.None;
            }
            return true;
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = (Map)parms.target;
            int days = Condition == DepthRimDefOf.DepthRim_AlgalBloom ? Rand.RangeInclusive(3, 7) : Rand.RangeInclusive(5, 15);
            GameCondition cond = GameConditionMaker.MakeCondition(Condition, days * GenDate.TicksPerDay);
            map.gameConditionManager.RegisterCondition(cond);
            SendStandardLetter(def.letterLabel, def.letterText, def.letterDef, parms, new LookTargets(MapComponent_Aquaculture.For(map).Cages.FirstOrDefault()));
            return true;
        }
    }

    // ═══ 掠食者（§6.2）═══

    /// <summary>
    /// 咬網的掠食者：灣內 1～2 隻 Odyssey 水獺，外海 1 隻睡鯊。生成在有魚箱網旁邊的水面，咬到預算的傷害（30～60）就離開。
    /// </summary>
    public class IncidentWorker_CagePredator : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            Map map = parms.target as Map;
            MapComponent_Aquaculture comp = MapComponent_Aquaculture.For(map);
            return comp != null && MapComponent_Aquaculture.Enabled && comp.StockedCages() > 0 && KindFor(comp.Venue) != null;
        }

        public static PawnKindDef KindFor(CageVenue venue)
        {
            return venue == CageVenue.Bay ? DefDatabase<PawnKindDef>.GetNamedSilentFail("Otter") : DepthRimDefOf.DepthRim_SleeperShark;
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = (Map)parms.target;
            MapComponent_Aquaculture comp = MapComponent_Aquaculture.For(map);
            List<Building_NetCage> stocked = comp.Cages.Where(c => c.Spawned && c.Cage.HasFish).ToList();
            if (stocked.Count == 0)
            {
                return false;
            }
            Building_NetCage target = stocked.RandomElement();
            PawnKindDef kind = KindFor(comp.Venue);
            int n = comp.Venue == CageVenue.Bay ? Rand.RangeInclusive(1, 2) : 1;
            List<Pawn> spawned = new List<Pawn>();
            for (int i = 0; i < n; i++)
            {
                IntVec3 cell = SpawnCell(map, target, kind);
                if (!cell.IsValid)
                {
                    break;
                }
                Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, null, PawnGenerationContext.NonPlayer, map.Tile));
                if (kind == DepthRimDefOf.DepthRim_SleeperShark)
                {
                    p.TryGetComp<CompFishCatch>()?.SetVariant(DefDatabase<FishVariantDef>.GetNamedSilentFail("DepthRim_FishVariant_SleeperShark"));
                }
                GenSpawn.Spawn(p, cell, map);
                if (kind == DepthRimDefOf.DepthRim_SleeperShark)
                {
                    p.TryGetComp<CompFishCatch>()?.SetVariant(DefDatabase<FishVariantDef>.GetNamedSilentFail("DepthRim_FishVariant_SleeperShark"));
                }
                CompNetRaider raider = p.TryGetComp<CompNetRaider>();
                raider?.Activate(target, Rand.RangeInclusive(30, 60));
                spawned.Add(p);
            }
            if (spawned.Count == 0)
            {
                return false;
            }
            SendStandardLetter(def.letterLabel, def.letterText.Formatted(spawned[0].KindLabel.Named("PREDATOR")), def.letterDef, parms, new LookTargets(spawned));
            return true;
        }

        /// <summary>箱網外緣的水面格：可站立的優先（水獺可以游過去咬），沒有就任何海面格（睡鯊在不可通行的開放海面上直接咬相鄰的網）。</summary>
        public static IntVec3 SpawnCell(Map map, Building_NetCage cage, PawnKindDef kind)
        {
            List<IntVec3> water = cage.OccupiedRect().ExpandedBy(1).EdgeCells
                .Where(c => c.InBounds(map) && MapComponent_Aquaculture.IsSurfaceWater(map.terrainGrid.TerrainAt(c)) && c.GetFirstPawn(map) == null).ToList();
            List<IntVec3> standable = water.Where(c => c.Standable(map)).ToList();
            if (standable.Count > 0) { return standable.RandomElement(); }
            if (water.Count > 0) { return water.RandomElement(); }
            // 灣內沒有貼網的水面：從可以走到箱網的地方來
            if (CellFinder.TryFindRandomCellNear(cage.AccessCell(), map, 6, c => c.Standable(map) && c.GetFirstPawn(map) == null, out IntVec3 near))
            {
                return near;
            }
            return IntVec3.Invalid;
        }
    }

    public class CompProperties_NetRaider : CompProperties
    {
        public CompProperties_NetRaider()
        {
            compClass = typeof(CompNetRaider);
        }
    }

    /// <summary>
    /// 咬網行為：預設關閉（野生／馴服的水獺不受影響），只有掠食者事件生成的個體會開。
    /// 開啟後反覆對目標箱網發動近戰，打出預算的傷害或超過一天就離開地圖。
    /// </summary>
    public class CompNetRaider : ThingComp
    {
        private bool active;
        private Building_NetCage target;
        private int budget;
        private int startHp;
        private int startTick;

        public bool Active => active;

        public Building_NetCage Target => target;

        public int DamageDealt => target == null ? 0 : Mathf.Max(0, startHp - target.HitPoints);

        public void Activate(Building_NetCage cage, int damage)
        {
            active = true;
            target = cage;
            budget = damage;
            startHp = cage.HitPoints;
            startTick = Find.TickManager.TicksGame;
            Attack();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref active, "netRaiderActive");
            Scribe_References.Look(ref target, "netRaiderTarget");
            Scribe_Values.Look(ref budget, "netRaiderBudget");
            Scribe_Values.Look(ref startHp, "netRaiderStartHp");
            Scribe_Values.Look(ref startTick, "netRaiderStartTick");
        }

        private Pawn Pawn => (Pawn)parent;

        public override void CompTick()
        {
            base.CompTick();
            if (!active || !parent.IsHashIntervalTick(60))
            {
                return;
            }
            Pawn p = Pawn;
            if (!p.Spawned || p.Dead || p.Downed)
            {
                active = false;
                return;
            }
            if (target == null || !target.Spawned || target.Map != p.Map || DamageDealt >= budget || Find.TickManager.TicksGame - startTick > GenDate.TicksPerDay)
            {
                Leave();
                return;
            }
            if (p.CurJobDef != JobDefOf.AttackMelee)
            {
                Attack();
            }
        }

        private void Attack()
        {
            Pawn p = Pawn;
            if (!p.Spawned || target == null) { return; }
            Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            job.expiryInterval = 2000;
            job.checkOverrideOnExpire = true;
            p.jobs.StartJob(job, JobCondition.InterruptForced);
        }

        private void Leave()
        {
            active = false;
            Pawn p = Pawn;
            if (CompFishCatch.IsAquatic(p))
            {
                SeaEdge.Splash(p.Map, p.Position);
                EscapedFish.SwimOff(p);
                return;
            }
            if (RCellFinder.TryFindBestExitSpot(p, out IntVec3 exit, TraverseMode.ByPawn))
            {
                Job job = JobMaker.MakeJob(JobDefOf.Goto, exit);
                job.exitMapOnArrival = true;
                p.jobs.StartJob(job, JobCondition.InterruptForced);
            }
        }
    }
}
