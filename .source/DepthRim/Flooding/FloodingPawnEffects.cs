using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水位對 Pawn 的影響（設計稿 §2.2、§4.2），每 <see cref="MapComponent_Flooding.UpdateInterval"/> ticks 結算一次：
    /// <list type="bullet">
    /// <item>涉水：水位越高移動越慢（<c>DepthRim_Wading</c>，嚴重度 = 水位）。</item>
    /// <item>溺水：沒入水中（站立時水位 ≥ 75%，躺下時 ≥ 30%）且沒有潛水裝備的氧氣時累積 <c>DepthRim_Drowning</c>；離開水面後消退。</item>
    /// <item>水壓：沒入水中時，若「環境水壓抗性」低於目前水深層級的需求，累積 <c>DepthRim_Barotrauma</c>。</item>
    /// </list>
    /// 不需要呼吸的 Pawn（非肉體，例如機械體）不會溺水，但仍受水壓影響。
    /// 三種 hediff 都靠 HediffDef 上的 SeverityPerDay 自然消退，所以回到地表後也會消失。
    /// </summary>
    public static class FloodingPawnEffects
    {
        public const float SubmergedLevelStanding = FloodingMath.SubmergedLevelStanding;

        /// <summary>水密門判斷用：低於這個氣量比例就不算「能在水中呼吸」（不讓氣快用完的人再出艙）。</summary>
        public const float MinAirToLeave = 0.2f;

        public const float SubmergedLevelLying = 0.3f;

        // 恢復由 HediffDef 的 SeverityPerDay 處理（離開水下地圖後也會繼續消退），這裡的累積量已扣掉同一段時間的衰減。

        /// <summary>每次更新（250 ticks）加上的溺水嚴重度：扣掉衰減後淨增約 0.1，約 1 小時（2500 ticks）致死。</summary>
        private const float DrowningPerUpdate = 0.15f;

        /// <summary>每差一級水壓抗性，每次更新加上的水壓傷害（淨增約 0.02）。</summary>
        private const float BarotraumaPerUpdatePerTier = 0.025f;

        private static readonly List<Pawn> tmpPawns = new List<Pawn>();

        public static void Apply(Map map, MapComponent_Flooding flooding)
        {
            int requiredResistance = FloodingMath.RequiredPressureResistance(flooding.DepthTier);
            tmpPawns.Clear();
            tmpPawns.AddRange(map.mapPawns.AllPawnsSpawned);
            for (int i = 0; i < tmpPawns.Count; i++)
            {
                Pawn pawn = tmpPawns[i];
                if (pawn.Dead || !pawn.Spawned)
                {
                    continue;
                }
                float level = flooding.LevelAt(pawn.Position);
                ApplyWading(pawn, level);
                bool submerged = IsSubmerged(pawn, level);
                ApplyBreathing(pawn, submerged);
                if (!pawn.Dead)
                {
                    ApplyPressure(pawn, submerged, requiredResistance);
                }
            }
            tmpPawns.Clear();
        }

        /// <summary>能不能在水中呼吸：非血肉（機械體）不用呼吸；穿著潛水服且氣量 ≥ 20%。</summary>
        public static bool CanBreatheUnderwater(Pawn pawn)
        {
            if (!pawn.RaceProps.IsFlesh)
            {
                return true;
            }
            CompDivingAir air = CompDivingAir.WornBy(pawn);
            return air != null && air.AirPercent >= MinAirToLeave;
        }

        public static bool IsSubmerged(Pawn pawn, float level)
        {
            bool lying = pawn.Downed || pawn.GetPosture() != PawnPosture.Standing;
            return level >= (lying ? SubmergedLevelLying : SubmergedLevelStanding);
        }

        private static void ApplyWading(Pawn pawn, float level)
        {
            Hediff wading = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Wading);
            if (level < 0.05f)
            {
                if (wading != null)
                {
                    pawn.health.RemoveHediff(wading);
                }
                return;
            }
            if (wading == null)
            {
                wading = pawn.health.AddHediff(DepthRimDefOf.DepthRim_Wading);
            }
            wading.Severity = level;
        }

        private static void ApplyBreathing(Pawn pawn, bool submerged)
        {
            CompDivingAir air = CompDivingAir.WornBy(pawn);
            if (!submerged)
            {
                air?.Refill(MapComponent_Flooding.UpdateInterval);
                return;
            }
            if (!pawn.RaceProps.IsFlesh)
            {
                return;
            }
            if (air != null && air.TryConsume(MapComponent_Flooding.UpdateInterval))
            {
                FloodingAudio.PlayDiverBreath(pawn, air);
                return;
            }
            HealthUtility.AdjustSeverity(pawn, DepthRimDefOf.DepthRim_Drowning, DrowningPerUpdate);
        }

        private static void ApplyPressure(Pawn pawn, bool submerged, int requiredResistance)
        {
            int shortfall = submerged ? requiredResistance - (int)pawn.GetStatValue(DepthRimDefOf.DepthRim_PressureResistance) : 0;
            if (shortfall > 0)
            {
                HealthUtility.AdjustSeverity(pawn, DepthRimDefOf.DepthRim_Barotrauma, BarotraumaPerUpdatePerTier * shortfall);
            }
        }
    }
}
