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

    /// <summary>
    /// 水中射程（2026-11-16 使用者要求）：射手泡在水裡時，遠程武器射程只剩 <see cref="FloodingMath.UnderwaterRangeFactor"/>。
    /// 不用原版天氣的 maxRangeCap：那是整張地圖一起砍，連排乾的艙室裡也會被限制；這裡依射手所在格的水位判斷，
    /// 排乾的艙室（水位 0）不受影響，淹水艙室則和開放水域一樣。帶 <see cref="UnderwaterWeaponExtension"/> 的武器不受限。
    /// </summary>
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
