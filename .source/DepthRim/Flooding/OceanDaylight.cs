using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DepthRim
{

    public static class OceanDaylight
    {
        private static readonly Dictionary<int, float> uncapped = new Dictionary<int, float>();

        /// <summary>這個層級的日光上限。</summary>
        public static float CapFor(DepthTier tier)
        {
            return Mathf.Clamp01(WaterVisualTierDef.For(tier)?.maxSkyGlow ?? 1f);
        }

        /// <summary>水面上方的日光（限制前），水體視覺用；這張地圖還沒算過時用目前的天空光。</summary>
        public static float Uncapped(Map map)
        {
            if (map == null)
            {
                return 0f;
            }
            // 上限沒卡到時直接用原版平滑過的天空光（淺海一律如此）；卡到時才用限制前的目標值
            float current = map.skyManager.CurSkyGlow;
            if (current < CapOn(map) - 0.001f || !uncapped.TryGetValue(map.uniqueID, out float glow))
            {
                return current;
            }
            return Mathf.Max(current, glow);
        }

        /// <summary>
        /// 這張地圖現在的日光上限：換層淡入淡出中時在兩層的上限之間內插（<see cref="MapComponent_Flooding.TierFadeProgress"/>），
        /// 否則就是目前層級的上限。原版的天空亮度每幀直接等於目標值，所以不在這裡內插的話，換層時整張地圖的亮度會一下子跳過去。
        /// </summary>
        public static float CapOn(Map map)
        {
            MapComponent_Flooding flooding = map?.Flooding();
            if (flooding != null && flooding.FadingTier && flooding.debugTierOverride == null)
            {
                return Mathf.Lerp(CapFor(flooding.FadeFrom), CapFor(flooding.FadeTo), flooding.TierFadeProgress);
            }
            return CapFor(FloodingUtility.DepthTierOf(map));
        }

        internal static void Record(Map map, float glow)
        {
            uncapped[map.uniqueID] = glow;
        }
    }

    [HarmonyPatch(typeof(SkyManager), "CurrentSkyTarget")]
    public static class Patch_SkyManager_CurrentSkyTarget
    {
        private static readonly AccessTools.FieldRef<SkyManager, Map> MapField = AccessTools.FieldRefAccess<SkyManager, Map>("map");

        [HarmonyPostfix]
        public static void Postfix(SkyManager __instance, ref SkyTarget __result)
        {
            Map map = MapField(__instance);
            if (!FloodingUtility.IsWaterMap(map))
            {
                return;
            }
            OceanDaylight.Record(map, __result.glow);
            __result.glow = Mathf.Min(__result.glow, OceanDaylight.CapOn(map));
        }
    }
}
