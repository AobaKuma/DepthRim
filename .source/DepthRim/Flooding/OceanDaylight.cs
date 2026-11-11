using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海洋地圖的日光依水深限制（2026-11-11 使用者要求）：原版天空光（<c>SkyManager.CurSkyGlow</c>）決定黑暗判定、工作速度與植物生長，
    /// 原本深海、海溝在白天也有完整日光。現在乘不過該層級的 <see cref="WaterVisualTierDef.maxSkyGlow"/>：
    /// 淺海不限、變溫層微光、深海幾乎全黑、深海海床完全沒有日光。層級看 <see cref="FloodingUtility.DepthTierOf"/>，
    /// 所以潛艇的船體地圖跟著下潛深度變暗。
    /// <para>水體的樣子（外海層、深淵背景、暗處提示）照舊用限制前的日光乘上各層級的 ambientReach（<see cref="Uncapped"/>），
    /// 那些視覺已經依深度調好，不重複變暗。</para>
    /// </summary>
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
            if (current < CapFor(FloodingUtility.DepthTierOf(map)) - 0.001f || !uncapped.TryGetValue(map.uniqueID, out float glow))
            {
                return current;
            }
            return Mathf.Max(current, glow);
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
            __result.glow = Mathf.Min(__result.glow, OceanDaylight.CapFor(FloodingUtility.DepthTierOf(map)));
        }
    }
}
