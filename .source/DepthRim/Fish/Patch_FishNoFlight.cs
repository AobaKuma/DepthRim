using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DepthRim
{
    /// <summary>
    /// 魚只用原版的離場／進場飛行（游離、游進地圖，見 Fish_Races.xml 的 canLeaveMapFlying／canFlyIntoMap），
    /// 但不會在地圖上「飛」：原版有些工作（Wait_Combat、AttackMelee 等 overrideFlyChance = 1）會讓會飛的動物起飛，
    /// 而起飛要用 PawnKindDef 的飛行動畫，魚沒有，會在 CheckFlyAnimation 丟 NullReferenceException。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_FlightTracker), nameof(Pawn_FlightTracker.Notify_JobStarted))]
    public static class Patch_FishNoFlight_JobStarted
    {
        public static bool Prefix(Pawn ___pawn, Job job)
        {
            if (!CompFishCatch.IsAquatic(___pawn))
            {
                return true;
            }
            job.flying = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_FlightTracker), nameof(Pawn_FlightTracker.StartFlying))]
    public static class Patch_FishNoFlight_StartFlying
    {
        public static bool Prefix(Pawn ___pawn)
        {
            return !CompFishCatch.IsAquatic(___pawn);
        }
    }
}
