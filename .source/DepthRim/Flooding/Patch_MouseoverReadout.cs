using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 左下角的格子資訊加上水位（使用者要求「水位百分比顯示在左下角，和真空放一起」）。
    ///
    /// <para>原版 <c>MouseoverReadout.MouseoverReadoutOnGUI</c> 依序往上畫每一行（每行 19 px，偏移量存在區域變數 num）：
    /// 光照之後就是 Odyssey 的真空行。這裡在光照那行的 <c>num += 19f</c> 之後插入 <c>DrawWaterLine(ref num)</c>，
    /// 所以水位和真空在同一個位置、之後的地形、屋頂等行自動往上推。</para>
    ///
    /// <para>迷霧格原版在更前面就 return（只顯示「未探索」），不會洩漏水位。</para>
    /// </summary>
    [HarmonyPatch(typeof(MouseoverReadout), nameof(MouseoverReadout.MouseoverReadoutOnGUI))]
    public static class Patch_MouseoverReadout
    {
        private static readonly Vector2 BotLeft = new Vector2(15f, 65f);

        private static readonly MethodInfo DrawMethod = AccessTools.Method(typeof(Patch_MouseoverReadout), nameof(DrawWaterLine));

        private static float cachedLevel = -1f;

        private static bool cachedOpen;

        private static string cachedText;

        /// <summary>這一行要顯示的文字（沒有水位系統的地圖回傳 null）。給驗收測試與繪製共用。</summary>
        public static string LineFor(Map map, IntVec3 c)
        {
            MapComponent_Flooding flooding = map?.Flooding();
            if (flooding == null || !flooding.Active || !c.InBounds(map))
            {
                return null;
            }
            float level = flooding.LevelAt(c);
            bool open = flooding.IsOpenAt(c);
            if (!Mathf.Approximately(level, cachedLevel) || open != cachedOpen || cachedText == null)
            {
                cachedLevel = level;
                cachedOpen = open;
                cachedText = (open ? "DepthRim.Readout.WaterLevelOpen" : "DepthRim.Readout.WaterLevel").Translate(level.ToStringPercent("0"));
            }
            return cachedText;
        }

        public static void DrawWaterLine(ref float num)
        {
            string text = LineFor(Find.CurrentMap, UI.MouseCell());
            if (text == null)
            {
                return;
            }
            Widgets.Label(new Rect(BotLeft.x, UI.screenHeight - BotLeft.y - num, 999f, 999f), text);
            num += 19f;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            // 找第一個「ldloc num; ldc.r4 19; add; stloc num」（光照那行之後），在它後面插入 DrawWaterLine(ref num)
            for (int i = 1; i + 2 < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float f && f == 19f
                    && list[i + 1].opcode == OpCodes.Add && list[i + 2].IsStloc() && list[i - 1].IsLdloc())
                {
                    object local = list[i + 2].operand;
                    CodeInstruction loadAddress = local is LocalBuilder lb ? new CodeInstruction(OpCodes.Ldloca_S, lb) : LocalAddress(list[i + 2]);
                    if (loadAddress == null)
                    {
                        break;
                    }
                    list.Insert(i + 3, loadAddress);
                    list.Insert(i + 4, new CodeInstruction(OpCodes.Call, DrawMethod));
                    return list;
                }
            }
            Log.Error("[DepthRim] MouseoverReadoutOnGUI layout changed; the water level line will not be shown.");
            return list;
        }

        /// <summary>stloc.0–3 這種沒有運算元的寫法，換成對應的 ldloca.s。</summary>
        private static CodeInstruction LocalAddress(CodeInstruction stloc)
        {
            int index;
            if (stloc.opcode == OpCodes.Stloc_0) { index = 0; }
            else if (stloc.opcode == OpCodes.Stloc_1) { index = 1; }
            else if (stloc.opcode == OpCodes.Stloc_2) { index = 2; }
            else if (stloc.opcode == OpCodes.Stloc_3) { index = 3; }
            else { return null; }
            return new CodeInstruction(OpCodes.Ldloca_S, (byte)index);
        }
    }
}
