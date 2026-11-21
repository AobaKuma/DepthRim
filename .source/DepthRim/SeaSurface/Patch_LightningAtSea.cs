using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海上的雷擊（2026-11-21 使用者的 log）：原版 <c>WeatherEvent_LightningStrike.DoStrike</c> 沒有指定落點時，隨機找「能站、沒屋頂」的格子；
    /// 海面地圖幾乎全是開放海面（不能站），找不到就變成 (-1000, -1000, -1000)，生成爆炸時出錯並丟 NullReferenceException。
    /// 這裡先照原版的規則找；找不到就打在任何沒屋頂的格子（打在海上）；連那都沒有就不打（只有原版已經播過的遠處雷聲）。
    /// </summary>
    [HarmonyPatch(typeof(WeatherEvent_LightningStrike), nameof(WeatherEvent_LightningStrike.DoStrike))]
    public static class Patch_LightningAtSea
    {
        public static bool Prefix(ref IntVec3 strikeLoc, Map map, ref Mesh boltMesh)
        {
            if (strikeLoc.IsValid || map == null)
            {
                return true;
            }
            IntVec3 loc = CellFinderLoose.RandomCellWith(sq => sq.Standable(map) && !map.roofGrid.Roofed(sq), map);
            if (!loc.IsValid)
            {
                loc = CellFinderLoose.RandomCellWith(sq => !map.roofGrid.Roofed(sq), map);
            }
            if (!loc.IsValid)
            {
                return false;
            }
            strikeLoc = loc;
            return true;
        }
    }
}
