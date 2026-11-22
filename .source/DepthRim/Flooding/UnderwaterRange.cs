using HarmonyLib;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水下武器：掛在武器（或砲塔用槍）的 ThingDef 上，泡在水裡開火時射程不打折（<see cref="UnderwaterRange"/>）。
    /// </summary>
    public class UnderwaterWeaponExtension : DefModExtension
    {
    }
    public static class UnderwaterRange
    {
        public static bool IsUnderwaterWeapon(Verb verb)
        {
            ThingDef weapon = verb?.EquipmentSource?.def;
            return weapon != null && weapon.HasModExtension<UnderwaterWeaponExtension>();
        }

        /// <summary>攻擊者此刻是否在水中開火：Pawn 依淹沒判定（站著 75%、躺著 30%）；砲塔等建築以站立門檻算。</summary>
        public static bool AttackerSubmerged(Thing attacker)
        {
            if (attacker == null || !attacker.Spawned)
            {
                return false;
            }
            MapComponent_Flooding flooding = attacker.Map.Flooding();
            if (flooding == null || !flooding.HasWater)
            {
                return false;
            }
            float level = flooding.LevelAt(attacker.Position);
            return attacker is Pawn pawn ? FloodingPawnEffects.IsSubmerged(pawn, level) : level >= FloodingMath.SubmergedLevelStanding;
        }
    }

    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.AdjustedRange))]
    public static class Patch_VerbProperties_AdjustedRange
    {
        public static void Postfix(VerbProperties __instance, Verb ownerVerb, Thing attacker, ref float __result)
        {
            if (__instance.IsMeleeAttack || __result <= 0f)
            {
                return;
            }
            __result = FloodingMath.RangeInWater(__result, UnderwaterRange.AttackerSubmerged(attacker), UnderwaterRange.IsUnderwaterWeapon(ownerVerb));
        }
    }
}
