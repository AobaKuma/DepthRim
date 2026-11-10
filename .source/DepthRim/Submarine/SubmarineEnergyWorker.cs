using System;
using DMSE;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇的航程成本只有推進器在航行中的耗電（DMSE 的 <see cref="TransferEnergyWorker"/>，掛在海洋層）：
    /// <list type="bullet">
    /// <item>出航時不收一次性的燃料（整備檢查不擋、出航不從電池扣電）：<see cref="FuelRequired"/> 回傳 0。</item>
    /// <item>規劃畫面的估算改成以推進器耗電計：所需 = 推進器功率 × 航行時間，最大航程 = 船上電池能讓推進器跑多久。</item>
    /// </list>
    /// </summary>
    public class SubmarineEnergyWorker : TransferEnergyWorker
    {
        public const int MaxRangeTiles = 500;

        public override float FuelRequired(TransferShipSurvey survey, int distance)
        {
            return 0f;
        }

        public override void AdjustEstimate(TransferShipSurvey survey, TransferEstimate estimate)
        {
            float watts = SubmarineEnergy.ThrusterWatts(survey);
            float fixedHours = estimate.burnHours + estimate.arrivalBurnHours;
            int range = VoyageMath.RangeTiles(estimate.fuelAvailable * BatteryFuelSource.WattDaysPerFuel, watts, fixedHours, TransferFlightMath.Tuning.coastHoursPerTile);
            // 規劃畫面會畫出航程圈：太大的圈很慢，也沒有意義
            estimate.maxDistance = Math.Min(range, MaxRangeTiles);
            if (estimate.HasDestination)
            {
                estimate.fuelRequired = VoyageMath.EnergyWd(watts, estimate.totalHours) / BatteryFuelSource.WattDaysPerFuel;
            }
        }
    }

    public static class SubmarineEnergy
    {
        /// <summary>推進器全速時的總功率（W）：只算勘查判定可運作的推進器。</summary>
        public static float ThrusterWatts(TransferShipSurvey survey)
        {
            float watts = 0f;
            if (survey?.engines == null)
            {
                return 0f;
            }
            foreach (TransferShipSurvey.EngineEntry entry in survey.engines)
            {
                if (!entry.Operational)
                {
                    continue;
                }
                CompPowerTrader power = entry.engine?.parent?.TryGetComp<CompPowerTrader>();
                if (power != null)
                {
                    watts += power.Props.PowerConsumption;
                }
            }
            return watts;
        }
    }
}
