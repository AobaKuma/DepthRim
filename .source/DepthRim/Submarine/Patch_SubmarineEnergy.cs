using System;
using System.Reflection;
using DMSE;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇的航程成本只有推進器在航行中的耗電（和 DMSE 不同：DMSE 出航時一次扣掉整趟的燃料）。
    /// <list type="bullet">
    /// <item>出航檢查與出航本身（<c>TransferReadiness.Check</c>、<c>ShipSeparationUtility.Separate</c>）在海洋層上執行時，
    /// <c>TransferFlightMath.FuelRequired</c> 回傳 0：不擋出航、不從電池扣電。</item>
    /// <item>規劃畫面的估算（<c>TransferEstimate.For</c>）改成以推進器耗電計：所需 = 推進器功率 × 航行時間，
    /// 最大航程 = 船上電池能讓推進器跑多久。</item>
    /// </list>
    /// </summary>
    public static class SubmarineEnergy
    {
        [ThreadStatic]
        private static int noChargeDepth;

        public static bool NoCharge => noChargeDepth > 0;

        public static bool OnOcean(Map map)
        {
            return map != null && OceanRouting.IsOcean(map.Tile);
        }

        public static bool OnOcean(CompTransferConsole console)
        {
            return OnOcean(console?.parent?.MapHeld);
        }

        public static void Enter(bool active, ref bool state)
        {
            state = active;
            if (active)
            {
                noChargeDepth++;
            }
        }

        public static void Exit(bool state)
        {
            if (state && noChargeDepth > 0)
            {
                noChargeDepth--;
            }
        }

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

    [HarmonyPatch(typeof(TransferFlightMath), nameof(TransferFlightMath.FuelRequired))]
    public static class Patch_SubmarineEnergy_FuelRequired
    {
        [HarmonyPrefix]
        public static bool Prefix(ref float __result)
        {
            if (!SubmarineEnergy.NoCharge)
            {
                return true;
            }
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(TransferReadiness), nameof(TransferReadiness.Check))]
    public static class Patch_SubmarineEnergy_Readiness
    {
        [HarmonyPrefix]
        public static void Prefix(CompTransferConsole console, out bool __state)
        {
            __state = false;
            SubmarineEnergy.Enter(SubmarineEnergy.OnOcean(console), ref __state);
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            SubmarineEnergy.Exit(__state);
        }
    }

    [HarmonyPatch]
    public static class Patch_SubmarineEnergy_Separate
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("DMSE.ShipSeparationUtility"), "Separate");
        }

        [HarmonyPrefix]
        public static void Prefix(CompTransferConsole console, out bool __state)
        {
            __state = false;
            SubmarineEnergy.Enter(SubmarineEnergy.OnOcean(console), ref __state);
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            SubmarineEnergy.Exit(__state);
        }
    }

    [HarmonyPatch(typeof(TransferEstimate), nameof(TransferEstimate.For))]
    public static class Patch_SubmarineEnergy_Estimate
    {
        public const int MaxRangeTiles = 500;

        [HarmonyPostfix]
        public static void Postfix(TransferShipSurvey survey, TransferEstimate __result)
        {
            if (__result == null || !SubmarineEnergy.OnOcean(survey?.network?.map))
            {
                return;
            }
            float watts = SubmarineEnergy.ThrusterWatts(survey);
            float fixedHours = __result.burnHours + __result.arrivalBurnHours;
            int range = VoyageMath.RangeTiles(__result.fuelAvailable * BatteryFuelSource.WattDaysPerFuel, watts, fixedHours, TransferFlightMath.Tuning.coastHoursPerTile);
            // 規劃畫面會畫出航程圈：太大的圈很慢，也沒有意義
            __result.maxDistance = Math.Min(range, MaxRangeTiles);
            if (__result.HasDestination)
            {
                __result.fuelRequired = VoyageMath.EnergyWd(watts, __result.totalHours) / BatteryFuelSource.WattDaysPerFuel;
            }
        }
    }
}
