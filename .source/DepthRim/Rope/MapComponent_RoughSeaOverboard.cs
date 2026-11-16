using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水面惡劣海況的落海（設計稿 §3.2）：海面地圖的水面層、天氣倍率達到 <see cref="RopeTetherMath.RoughSeaFactor"/> 時，
    /// 每 <see cref="CheckInterval"/> ticks 對每個艙外、會被沖走的人擲一次（平均間隔見 <see cref="RopeTetherMath.RoughSeaMtbHours"/>）。
    /// 沒扣繩的人和航行中落海一樣改為失蹤、寄信；扣著繩的人被浪打倒（暈眩、水花），不失蹤。平台層浪打不到，不判定。
    /// </summary>
    public class MapComponent_RoughSeaOverboard : MapComponent
    {
        public const int CheckInterval = 250;

        /// <summary>扣著繩的人被浪打中時暈眩多久（ticks）。</summary>
        public const int KnockdownStunTicks = 120;

        /// <summary>升級後第一次載入正逢惡劣海況時的寬限（ticks，§6）。</summary>
        public const int UpgradeGraceTicks = 2500;

        /// <summary>測試用：每次擲骰都中。</summary>
        public static bool debugAlwaysHit;

        private HazardKind lastHazard;

        /// <summary>存檔版本：舊存檔沒有這個值（0），讀進來時給寬限期。</summary>
        private int version = 1;

        private int graceUntilTick = -1;

        private readonly List<Pawn> tmpPawns = new List<Pawn>();

        /// <summary>給測試：被浪打倒（扣著繩）的次數。</summary>
        public int Knockdowns { get; private set; }

        public MapComponent_RoughSeaOverboard(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref version, "version", 0);
            Scribe_Values.Look(ref lastHazard, "lastHazard", HazardKind.None);
            Scribe_Values.Look(ref graceUntilTick, "graceUntilTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && version == 0)
            {
                graceUntilTick = Find.TickManager.TicksGame + UpgradeGraceTicks;
                version = 1;
            }
        }

        public override void MapComponentTick()
        {
            // 和航行中落海、扣繩掃描錯開
            if ((Find.TickManager.TicksGame + map.uniqueID + 17) % CheckInterval != 0)
            {
                return;
            }
            if (!SeaLevels.IsSeaLevel(map))
            {
                return;
            }
            HazardKind now = OverboardHazard.HazardOf(map);
            if (now == HazardKind.RoughSea && lastHazard != HazardKind.RoughSea)
            {
                Messages.Message("DepthRim.RoughSea.Started".Translate(), new LookTargets(map.Parent), MessageTypeDefOf.CautionInput, historical: false);
            }
            lastHazard = now;
            if (now != HazardKind.RoughSea || Find.TickManager.TicksGame < graceUntilTick)
            {
                return;
            }
            MapComponent_RopeTethers.For(map)?.Scan();
            tmpPawns.Clear();
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                if (VoyageOverboard.CanBeSwept(p) && VoyageOverboard.IsOutside(p))
                {
                    tmpPawns.Add(p);
                }
            }
            if (tmpPawns.Count > 0)
            {
                Roll(tmpPawns, forceHit: false);
            }
            tmpPawns.Clear();
        }

        /// <summary>
        /// 對清單上的人各擲一次（<paramref name="forceHit"/> 時直接算被浪打中：錨點被打壞、繩子斷開的那一刻）。
        /// 回傳失蹤的人。
        /// </summary>
        public List<Pawn> Roll(List<Pawn> pawns, bool forceHit)
        {
            List<Pawn> lost = new List<Pawn>();
            float factor = OverboardHazard.SeaFactor(map);
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || !p.Spawned || p.Map != map || !VoyageOverboard.CanBeSwept(p) || !VoyageOverboard.IsOutside(p))
                {
                    continue;
                }
                bool hit = forceHit || debugAlwaysHit || Rand.MTBEventOccurs(MtbHoursFor(p, factor), GenDate.TicksPerHour, CheckInterval);
                if (!hit)
                {
                    continue;
                }
                if (RopeTether.IsProtected(p))
                {
                    KnockDown(p);
                    continue;
                }
                // 扛著的人一起被捲走（扛的人沒扣繩）
                if (p.carryTracker?.CarriedThing != null && p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out Thing dropped)
                    && dropped is Pawn carried && VoyageOverboard.CanBeSwept(carried) && carried.Spawned && !RopeTether.IsProtected(carried))
                {
                    Splash(carried.Position);
                    VoyageOverboard.Lose(carried, map);
                    lost.Add(carried);
                }
                Splash(p.Position);
                VoyageOverboard.Lose(p, map);
                lost.Add(p);
            }
            List<Pawn> reported = lost.Where(VoyageOverboard.Reportable).ToList();
            if (reported.Count > 0)
            {
                Find.LetterStack.ReceiveLetter("DepthRim.RoughSea.OverboardLabel".Translate(),
                    "DepthRim.RoughSea.OverboardText".Translate(reported.Select(x => x.LabelCap).ToLineList("  - ")),
                    LetterDefOf.NegativeEvent, new LookTargets(map.Parent));
            }
            return lost;
        }

        /// <summary>這個人現在的平均間隔（遊戲小時）。</summary>
        public float MtbHoursFor(Pawn p, float factor)
        {
            bool nearEdge = NearOpenSea(p.Position);
            bool lowMoving = p.health?.capacities != null && p.health.capacities.GetLevel(PawnCapacityDefOf.Moving) < RopeTetherMath.LowMoving;
            return RopeTetherMath.RoughSeaMtbHours(factor, nearEdge, p.Downed, lowMoving);
        }

        /// <summary>2 格內有開放海面（浪直接打上來的地方）。</summary>
        public bool NearOpenSea(IntVec3 cell)
        {
            return SeaEdge.NearOpenSea(map, cell);
        }

        private void KnockDown(Pawn p)
        {
            Knockdowns++;
            p.stances?.stunner?.StunFor(KnockdownStunTicks, null, false);
            Splash(p.Position);
        }

        private void Splash(IntVec3 cell)
        {
            EffecterDef def = DefDatabase<EffecterDef>.GetNamedSilentFail("PawnEmergeFromWaterLarge");
            if (def == null || !cell.InBounds(map))
            {
                return;
            }
            Effecter effecter = def.Spawn(cell, map);
            map.effecterMaintainer.AddEffecterToMaintain(effecter, cell, 60);
        }
    }
}
