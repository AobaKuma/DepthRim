using System;
using System.Collections.Generic;
using DMSE;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 龍骨不畫「鋼樑連結線」（2026-11-22 使用者要求）：DMSE 選取結構樁時，
    /// <see cref="PileDrawUtility.DrawNetworkOverlay"/> 會把整個龍骨網路的每一條連結畫成一條線。
    /// DepthRim 的龍骨一船好幾座，選取時整片都是線，看起來像雜訊。
    ///
    /// <para>做法：只在網路裡有 DepthRim 龍骨時，於繪製期間暫時清空 <see cref="PileNetwork.links"/>
    /// （readonly 清單，內容只是連結兩端的 <see cref="CompStructuralPile"/>），畫完（含例外）原樣放回；
    /// 船體範圍、爭議格、斷裂格與未連結樁的提示照舊由 DMSE 畫。DMSE 自己的結構樁（太空船）不受影響。
    /// 同一幀已經畫過（<see cref="PileNetwork.lastOverlayFrame"/>）就不再動。</para>
    /// </summary>
    [HarmonyPatch(typeof(PileDrawUtility), nameof(PileDrawUtility.DrawNetworkOverlay))]
    public static class Patch_KeelLinkLines
    {
        private const string KeelDefPrefix = "DepthRim_Keel";

        private static void Prefix(PileNetwork net, out List<PileNetwork.Link> __state)
        {
            __state = null;
            if (net == null || net.links.Count == 0 || net.lastOverlayFrame == Time.frameCount || !HasKeel(net))
            {
                return;
            }
            __state = new List<PileNetwork.Link>(net.links);
            net.links.Clear();
        }

        private static Exception Finalizer(PileNetwork net, List<PileNetwork.Link> __state, Exception __exception)
        {
            if (__state != null)
            {
                net.links.Clear();
                net.links.AddRange(__state);
            }
            return __exception;
        }

        /// <summary>這個網路裡有沒有 DepthRim 的龍骨框架（大龍骨與小龍骨都算；DMSE 的太空結構樁不算）。</summary>
        private static bool HasKeel(PileNetwork net)
        {
            for (int i = 0; i < net.piles.Count; i++)
            {
                ThingDef def = net.piles[i]?.parent?.def;
                if (def?.defName != null && def.defName.StartsWith(KeelDefPrefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
