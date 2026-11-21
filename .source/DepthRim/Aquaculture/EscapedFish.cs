using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using RimWorld.Planet;
using Verse.AI;

namespace DepthRim
{
    /// <summary>
    /// 逃逸的養殖魚（§6.6）：最多 10 隻 Pawn，生成在箱網外緣的水面格、帶養殖品種的顏色與名稱。
    /// 可站立的水（灣內淺水）留下 2 天可獵殺，之後游走（<see cref="MapComponent_EscapedFish"/>）；
    /// 不可站立的水（開放海面、原版深水）一生成就用原版的 ExitMapFlying 游離，不留在不可通行格上。
    /// </summary>
    public static class EscapedFish
    {
        public const int StayTicks = 2 * GenDate.TicksPerDay;

        public static PawnKindDef KindFor(FishVariantDef variant)
        {
            switch (variant?.size ?? FishSize.Medium)
            {
                case FishSize.Small: return PawnKindDef.Named("DepthRim_Fish_Small");
                case FishSize.Large: return PawnKindDef.Named("DepthRim_Fish_Large");
                default: return PawnKindDef.Named("DepthRim_Fish_Medium");
            }
        }

        public static int Spawn(Map map, CellRect cage, AquacultureSpeciesDef species, int n)
        {
            if (map == null || species == null || n <= 0)
            {
                return 0;
            }
            List<IntVec3> cells = new List<IntVec3>();
            foreach (IntVec3 c in cage.ExpandedBy(1).EdgeCells)
            {
                if (c.InBounds(map) && MapComponent_Aquaculture.IsSurfaceWater(map.terrainGrid.TerrainAt(c)))
                {
                    cells.Add(c);
                }
            }
            if (cells.Count == 0)
            {
                return 0;
            }
            PawnKindDef kind = KindFor(species.variant);
            MapComponent_EscapedFish tracker = map.GetComponent<MapComponent_EscapedFish>();
            int spawned = 0;
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = cells.RandomElement();
                Pawn fish = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, null, PawnGenerationContext.NonPlayer, map.Tile));
                fish.TryGetComp<CompFishCatch>()?.SetVariant(species.variant);
                GenSpawn.Spawn(fish, c, map);
                fish.TryGetComp<CompFishCatch>()?.SetVariant(species.variant);
                spawned++;
                if (c.Standable(map))
                {
                    tracker?.Track(fish);
                }
                else
                {
                    SwimOff(fish);
                }
            }
            return spawned;
        }

        public static void SwimOff(Pawn fish)
        {
            if (fish.Spawned && fish.jobs != null && fish.RaceProps.canLeaveMapFlying && fish.flight != null && fish.flight.CanEverFly)
            {
                fish.jobs.StartJob(JobMaker.MakeJob(JobDefOf.ExitMapFlying), JobCondition.InterruptForced);
            }
            if (fish.Spawned && fish.CurJobDef != JobDefOf.ExitMapFlying)
            {
                // 飛不了（例如在屋頂下）就直接游走
                fish.DeSpawn();
                Find.WorldPawns.PassToWorld(fish, PawnDiscardDecideMode.Discard);
            }
        }
    }

    /// <summary>可獵殺的逃逸魚：2 天後還在就游走。</summary>
    public class MapComponent_EscapedFish : MapComponent
    {
        private List<Pawn> fish = new List<Pawn>();
        private List<int> since = new List<int>();

        public MapComponent_EscapedFish(Map map) : base(map)
        {
        }

        public int Tracked => fish.Count;

        public void Track(Pawn p)
        {
            fish.Add(p);
            since.Add(Find.TickManager.TicksGame);
            KeepInWater(p);
        }

        public override void MapComponentTick()
        {
            if (fish.Count == 0 || !map.IsHashIntervalTick(15))
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = fish.Count - 1; i >= 0; i--)
            {
                Pawn p = fish[i];
                if (p == null || p.Dead || !p.Spawned || p.Map != map)
                {
                    fish.RemoveAt(i);
                    since.RemoveAt(i);
                    continue;
                }
                if (now - since[i] >= EscapedFish.StayTicks)
                {
                    fish.RemoveAt(i);
                    since.RemoveAt(i);
                    EscapedFish.SwimOff(p);
                    continue;
                }
                KeepInWater(p);
            }
        }

        /// <summary>
        /// 沒有水位的地圖上，原版動物 AI 不知道魚只能待在水裡：逃逸魚停在生成的水面格上，不會游上岸窒息。
        /// </summary>
        private void KeepInWater(Pawn p)
        {
            // 原版尋路不知道魚只能在水裡（會繞過岸邊），所以逃逸魚停在原地的水面上浮著，不自己移動
            if (p.Downed || p.jobs == null || p.CurJobDef == JobDefOf.ExitMapFlying || p.CurJobDef == JobDefOf.Wait)
            {
                return;
            }
            Job job = JobMaker.MakeJob(JobDefOf.Wait, 600);
            p.jobs.StartJob(job, JobCondition.InterruptForced);
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref fish, "fish", LookMode.Reference);
            Scribe_Collections.Look(ref since, "since", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                fish = fish ?? new List<Pawn>();
                since = since ?? new List<int>();
                while (since.Count < fish.Count) { since.Add(Find.TickManager.TicksGame); }
                fish.RemoveAll(p => p == null);
                while (since.Count > fish.Count) { since.RemoveAt(since.Count - 1); }
            }
        }
    }
}
