using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace DepthRim
{
    /// <summary>
    /// 航行中落海：潛艇在航行（<see cref="WorldObject_Submarine.InFlight"/> 且推進器有在推、不是停在海中漂流）時，
    /// 待在艙外的人（我方、訪客、商隊、敵人、野生動物都算；水生的魚除外）跟不上船，被沖離船身而失蹤。
    ///
    /// <para><b>艙外</b>：所在的房間是開放水域（沒有房間、碰到地圖邊緣、或有沒蓋屋頂的格子，同 <see cref="MapComponent_PressureChambers.IsOutside"/>）。
    /// 航行地圖上船體以外全是深淵，船體上沒蓋屋頂的甲板也算艙外；密閉艙室與氣閘裡面不算。</para>
    ///
    /// <para><b>時機</b>：航行期間持續檢查（每 <see cref="CheckInterval"/> ticks）。出港那一刻留在船體以外的人 DMSE 已經留在原地圖，
    /// 這裡處理的是航行途中走出艙外的人。停船、漂流（推力為零）、下錨或上浮之後都不會觸發。</para>
    ///
    /// <para><b>結果</b>：和 DMSE 被擊落載具的乘員一樣改為失蹤（世界 pawn 永久保留、其他殖民者得到「失蹤」的想法），寄一封信。</para>
    ///
    /// <para><b>安全繩</b>：扣在錨點上的人（與他們扛著的人）不會被沖走（<see cref="RopeTether.IsProtected"/>，
    /// Docs/安全繩錨點_機制設計.md）。</para>
    /// </summary>
    public static class VoyageOverboard
    {
        public const int CheckInterval = 250;

        /// <summary>船在動：航行中、地圖還在、推進器有推力。</summary>
        public static bool Underway(WorldObject_Submarine sub)
        {
            return sub != null && !sub.Destroyed && sub.HasMap && sub.InFlight && !sub.Stalled;
        }

        /// <summary>會被沖走的 pawn：活著、不是水生（魚本來就住在海裡）。</summary>
        public static bool CanBeSwept(Pawn pawn)
        {
            return pawn != null && !pawn.Dead && !CompFishCatch.IsAquatic(pawn);
        }

        /// <summary>寫進信裡的人：我方，或任何人形（訪客、商人、敵人）。野生動物安靜消失。</summary>
        public static bool Reportable(Pawn pawn)
        {
            return IsOurs(pawn) || pawn.RaceProps.Humanlike;
        }

        /// <summary>這個 pawn 是不是我方（殖民者、囚犯、奴隸、動物、機械體）。</summary>
        public static bool IsOurs(Pawn pawn)
        {
            return pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony;
        }

        /// <summary>站在艙外（開放水域的房間或沒有房間）。</summary>
        public static bool IsOutside(Pawn pawn)
        {
            return pawn.Spawned && MapComponent_PressureChambers.IsOutside(pawn.Position.GetRoom(pawn.Map));
        }

        /// <summary>重複使用的收集清單：沒有人在艙外時不配置記憶體（安全繩設計稿 §7.6）。</summary>
        private static readonly List<Pawn> tmpOutside = new List<Pawn>();

        private static readonly List<Pawn> NoneLost = new List<Pawn>();

        /// <summary>
        /// 航行中時，把艙外、沒扣安全繩的人改成失蹤並寄信。回傳失蹤的人（沒有時是共用的空清單，不要修改）。
        /// 檢查前先讓安全繩掃描一次，剛走出艙的人先扣上（<see cref="MapComponent_RopeTethers"/>）。
        /// </summary>
        public static List<Pawn> Sweep(WorldObject_Submarine sub)
        {
            if (!Underway(sub))
            {
                return NoneLost;
            }
            Map map = sub.Map;
            MapComponent_RopeTethers.For(map)?.Scan();
            tmpOutside.Clear();
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                if (CanBeSwept(p) && IsOutside(p) && !RopeTether.IsProtected(p))
                {
                    tmpOutside.Add(p);
                }
            }
            if (tmpOutside.Count == 0)
            {
                return NoneLost;
            }
            return SweepPawns(map, tmpOutside, sub);
        }

        /// <summary>
        /// 把清單上還在艙外、沒有受保護的人改成失蹤並寄信（安全繩斷開時也用，§2.5）。
        /// 清單會被改動（扛著的人放下後加進來）。
        /// </summary>
        public static List<Pawn> SweepPawns(Map map, List<Pawn> outside, WorldObject_Submarine sub)
        {
            List<Pawn> lost = new List<Pawn>();
            for (int i = outside.Count - 1; i >= 0; i--)
            {
                Pawn p = outside[i];
                if (p == null || !p.Spawned || p.Map != map || !CanBeSwept(p) || !IsOutside(p) || RopeTether.IsProtected(p))
                {
                    outside.RemoveAt(i);
                }
            }
            for (int i = 0; i < outside.Count; i++)
            {
                Pawn pawn = outside[i];
                if (!pawn.Spawned)
                {
                    continue;
                }
                // 扛著的人或物先放下：東西沉在原地，扛著的人同樣在艙外，一起失蹤
                if (pawn.carryTracker?.CarriedThing != null && pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out Thing dropped)
                    && dropped is Pawn carried && CanBeSwept(carried) && carried.Spawned && IsOutside(carried) && !outside.Contains(carried))
                {
                    outside.Add(carried);
                }
                Lose(pawn, map);
                lost.Add(pawn);
            }
            List<Pawn> reported = lost.Where(Reportable).ToList();
            if (reported.Count > 0)
            {
                Find.LetterStack.ReceiveLetter("DepthRim.Voyage.OverboardLabel".Translate(),
                    "DepthRim.Voyage.OverboardText".Translate(sub.Label, reported.Select(p => p.LabelCap).ToLineList("  - ")),
                    LetterDefOf.NegativeEvent, sub);
            }
            return lost;
        }

        /// <summary>移出地圖改為失蹤（不是逃跑、不是死亡）：做法同 DMSE 被擊落載具裡的乘員。</summary>
        internal static void Lose(Pawn pawn, Map map)
        {
            pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ExitedMap);
            pawn.Ideo?.Notify_MemberLost(pawn, map);
            pawn.DeSpawn(DestroyMode.Vanish);
            pawn.ownership?.UnclaimAll();
            // 我方的人永久保留（日後可能尋回）；其他人交給世界 pawn 自行決定去留
            Find.WorldPawns.PassToWorld(pawn, IsOurs(pawn) ? PawnDiscardDecideMode.KeepForever : PawnDiscardDecideMode.Decide);
            PawnDiedOrDownedThoughtsUtility.TryGiveThoughts(pawn, null, PawnDiedOrDownedThoughtsKind.Lost);
        }
    }
}
