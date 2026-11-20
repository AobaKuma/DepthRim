using HarmonyLib;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水下不能使用風力發電：原版風力發電機（<c>RimWorld.CompPowerPlantWind</c>）在水下地圖上一律不發電。
    ///
    /// <para>為什麼要補丁：水下地圖（<see cref="FloodingUtility.IsWaterMap"/>，冰洋層與潛艇地圖）整片都在岩頂底下，
    /// 原版的風道每一格都被屋頂擋住，本來就幾乎不會發電；但那是「剛好算出來是 0」，玩家看不出原因，
    /// 拆掉屋頂或某些地形下還可能發出電。這裡直接讓輸出歸零，並在檢視面板寫明原因，</para>
    ///
    /// <para>另外 <see cref="PlaceWorker_NoWindUnderwater"/> 會擋住放置，讓玩家在建之前就知道水下不能蓋風力發電機。</para>
    ///
    /// <para>只擋「水下地圖」：海面地圖（浮在海上的平台）不是 <see cref="FloodingUtility.IsWaterMap"/>，風力發電照常。</para>
    /// </summary>
    internal static class WindTurbineUnderwater
    {
        public static bool IsUnderwater(CompPowerPlantWind comp)
        {
            Thing parent = comp?.parent;
            if (parent == null || !parent.Spawned || !FloodingUtility.IsWaterMap(parent.Map))
            {
                return false;
            }
            MapComponent_Flooding flooding = parent.Map.Flooding();
            return flooding != null && flooding.Active;
        }
    }

    /// <summary>輸出歸零（<c>DesiredPowerOutput</c> 的 getter）。電力網每 tick 都經由 <c>UpdateDesiredPowerOutput</c> 讀這個值。</summary>
    [HarmonyPatch(typeof(CompPowerPlantWind), "DesiredPowerOutput", MethodType.Getter)]
    public static class Patch_WindTurbineUnderwater_DesiredPowerOutput
    {
        public static bool Prefix(CompPowerPlantWind __instance, ref float __result)
        {
            if (!WindTurbineUnderwater.IsUnderwater(__instance))
            {
                return true;
            }
            __result = 0f;
            return false;
        }
    }

    /// <summary>
    /// 把原版快取的輸出與本 tick 已寫進電力網的輸出一起歸零，扇葉就不會繼續轉、輸出條也會見底。
    /// （<c>CompPowerPlantWind.CompTick</c> 在算完風速後才呼叫 <c>UpdateDesiredPowerOutput</c> 的前一輪，
    /// 所以光靠 getter 補丁還留著一個 tick 的殘值。）
    /// </summary>
    [HarmonyPatch(typeof(CompPowerPlantWind), nameof(CompPowerPlantWind.CompTick))]
    public static class Patch_WindTurbineUnderwater_CompTick
    {
        public static void Postfix(CompPowerPlantWind __instance, ref float ___cachedPowerOutput)
        {
            if (!WindTurbineUnderwater.IsUnderwater(__instance))
            {
                return;
            }
            ___cachedPowerOutput = 0f;
            __instance.PowerOutput = 0f;
        }
    }

    /// <summary>水下不畫扇葉與輸出條（凍在原地的扇葉看起來像機械故障）。建築本體照常由 <c>Thing.Draw</c> 畫。</summary>
    [HarmonyPatch(typeof(CompPowerPlantWind), nameof(CompPowerPlantWind.PostDraw))]
    public static class Patch_WindTurbineUnderwater_PostDraw
    {
        public static bool Prefix(CompPowerPlantWind __instance)
        {
            return !WindTurbineUnderwater.IsUnderwater(__instance);
        }
    }

    /// <summary>檢視面板寫明「水下沒有風」。</summary>
    [HarmonyPatch(typeof(CompPowerPlantWind), nameof(CompPowerPlantWind.CompInspectStringExtra))]
    public static class Patch_WindTurbineUnderwater_Inspect
    {
        public static void Postfix(CompPowerPlantWind __instance, ref string __result)
        {
            if (!WindTurbineUnderwater.IsUnderwater(__instance))
            {
                return;
            }
            string line = "DepthRim.WindTurbine.NoWindUnderwaterInspect".Translate().Colorize(ColoredText.WarningColor);
            __result = __result.NullOrEmpty() ? line : __result + "\n" + line;
        }
    }
}
