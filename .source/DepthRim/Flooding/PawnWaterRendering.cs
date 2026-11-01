using System.Collections.Concurrent;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 依水位從下往上改變 Pawn 顏色（設計稿 §2.2）。只用在密閉艙室裡的積水；開放水域的 Pawn 由外海層整個蓋住。
    ///
    /// <para><b>做法：</b>每個渲染節點（身體、衣物、頭、頭髮、頭飾）的材質換成原版的 CutoutComplex 著色器，
    /// 配上一張垂直漸層遮罩：水線以上為紅色（原版遮罩的紅色 = 主色，維持原色），水線以下為綠色（= 第二色，原色 × 水色）。
    /// 不需要自訂著色器（1.7 的著色器格式不同，無法隨模組附帶）。</para>
    ///
    /// <para><b>水線換算：</b>把 Pawn 全身高度視為 0–1，人形的身體節點約佔 0–0.72、頭部節點約佔 0.62–1；
    /// 再換算成各節點貼圖的 UV 高度（貼圖上下有留白）。躺著的 Pawn 沒有「高度」，水位到沒頂門檻就整個上色。</para>
    ///
    /// <para><b>限制：</b>原本就用 CutoutComplex 的衣物（雙色衣物）在水中會失去第二色；
    /// 皮膚專用著色器的細節也會被換掉。離開水面後恢復原材質。</para>
    ///
    /// <para><b>執行緒：</b>原版在平行執行緒準備渲染（ParallelPreDraw），不能在那裡建立 Material。
    /// 第一次需要某個組合時先回傳原材質並排入佇列，由主執行緒（<see cref="ProcessPending"/>）建立，下一幀起生效。</para>
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PawnWaterRendering
    {
        private const int CutSteps = FloodingMath.CutSteps;

        private const int MaskHeight = 64;

        private static readonly Color WaterTint = new Color(0.35f, 0.55f, 0.9f);

        private static readonly Texture2D[] masks = new Texture2D[CutSteps + 1];

        private static readonly ConcurrentDictionary<(Material, int), Material> cache = new ConcurrentDictionary<(Material, int), Material>();

        private static readonly ConcurrentDictionary<(Material, int), byte> pending = new ConcurrentDictionary<(Material, int), byte>();

        static PawnWaterRendering()
        {
            for (int step = 0; step <= CutSteps; step++)
            {
                masks[step] = MakeMask((float)step / CutSteps);
            }
        }

        /// <summary>寬 1、高 64 的遮罩：v 低於 cut 的部分為綠色（第二色），以上為紅色（主色），交界處有 2 像素柔邊。</summary>
        private static Texture2D MakeMask(float cut)
        {
            Texture2D tex = new Texture2D(1, MaskHeight, TextureFormat.RGBA32, mipChain: false)
            {
                name = "DepthRim_WaterMask_" + cut.ToString("0.00"),
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            float cutPixel = cut * MaskHeight;
            for (int y = 0; y < MaskHeight; y++)
            {
                float submerged = Mathf.Clamp01((cutPixel - y) / 2f + 0.5f);
                tex.SetPixel(0, y, new Color(1f - submerged, submerged, 0f, 1f));
            }
            tex.Apply();
            return tex;
        }

        /// <summary>這個節點目前的水線（貼圖 UV 高度量化後的階數）；0 = 不在水中。</summary>
        public static int CutStepFor(PawnRenderNode node, PawnDrawParms parms)
        {
            Pawn pawn = parms.pawn;
            if (pawn == null || parms.Portrait || parms.Cache || !pawn.Spawned)
            {
                return 0;
            }
            // 開放水域與迷霧區裡的 Pawn 已經被外海層蓋住，只在密閉艙室裡畫水線
            if (pawn.Map.Flooding()?.IsOpenAt(pawn.Position) ?? false)
            {
                return 0;
            }
            float level = pawn.Position.WaterLevelAt(pawn.Map);
            bool standing = parms.posture == PawnPosture.Standing;
            bool head = pawn.RaceProps.Humanlike && IsHeadNode(node);
            return FloodingMath.CutStep(level, pawn.RaceProps.Humanlike, head, standing, FloodingPawnEffects.SubmergedLevelLying);
        }

        private static bool IsHeadNode(PawnRenderNode node)
        {
            for (PawnRenderNode n = node; n != null; n = n.parent)
            {
                if (n.Props?.tagDef == PawnRenderNodeTagDefOf.Head)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>取得水線材質；尚未建立時排入佇列並回傳 null（呼叫端沿用原材質）。可在任何執行緒呼叫。</summary>
        public static Material GetOrQueue(Material original, int step)
        {
            if (cache.TryGetValue((original, step), out Material mat))
            {
                return mat;
            }
            pending.TryAdd((original, step), 0);
            return null;
        }

        /// <summary>主執行緒：建立佇列中的水線材質。</summary>
        public static void ProcessPending()
        {
            if (pending.IsEmpty)
            {
                return;
            }
            foreach ((Material original, int step) key in pending.Keys)
            {
                pending.TryRemove(key, out _);
                Material original = key.original;
                if (original == null || cache.ContainsKey(key))
                {
                    continue;
                }
                Texture mainTex = original.mainTexture;
                if (mainTex == null)
                {
                    cache[key] = original;
                    continue;
                }
                Color color = original.color;
                MaterialRequest req = new MaterialRequest(mainTex, ShaderDatabase.CutoutComplex, color)
                {
                    colorTwo = color * WaterTint,
                    maskTex = masks[key.step]
                };
                cache[key] = MaterialPool.MatFrom(req);
            }
        }
    }

    /// <summary>節點材質：在水中時換成水線材質。</summary>
    [HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.GetFinalizedMaterial))]
    public static class Patch_PawnRenderNodeWorker_GetFinalizedMaterial
    {
        [HarmonyPostfix]
        public static void Postfix(PawnRenderNode node, PawnDrawParms parms, ref Material __result)
        {
            if (__result == null || parms.flags.FlagSet(PawnRenderFlags.Invisible))
            {
                return;
            }
            int step = PawnWaterRendering.CutStepFor(node, parms);
            if (step <= 0)
            {
                return;
            }
            Material water = PawnWaterRendering.GetOrQueue(__result, step);
            if (water != null)
            {
                __result = water;
            }
        }
    }

    /// <summary>
    /// 原版拉遠鏡頭時人形 Pawn 改畫快取好的整張貼圖，不會經過節點材質。
    /// 在水中的 Pawn 一律不用快取，才能套用水線。
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), "ParallelGetPreRenderResults")]
    public static class Patch_PawnRenderer_ParallelGetPreRenderResults
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn ___pawn, ref bool disableCache)
        {
            if (!disableCache && ___pawn != null && ___pawn.Spawned && ___pawn.Position.WaterLevelAt(___pawn.Map) > FloodingMath.RenderMinLevel
                && !(___pawn.Map.Flooding()?.IsOpenAt(___pawn.Position) ?? false))
            {
                disableCache = true;
            }
        }
    }
}
