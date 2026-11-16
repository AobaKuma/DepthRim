using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇指令圖示（Textures/UI，2026-11-16）：航行指令用「航線規劃」，停船與下錨、停靠用「下錨」，
    /// 選單裡的深度選項依目標比目前淺／深／相同用「上浮」「下潛」「巡航」。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class SubmarineIcons
    {
        public static readonly Texture2D RoutePlanning = Load("UI/Route Planning");
        public static readonly Texture2D Anchoring = Load("UI/Anchoring");
        public static readonly Texture2D Ascent = Load("UI/Ascent");
        public static readonly Texture2D Descent = Load("UI/Descent");
        public static readonly Texture2D Cruising = Load("UI/Cruising");

        private static Texture2D Load(string path) => ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.BadTex;

        /// <summary>從 <paramref name="from"/> 換到 <paramref name="to"/> 的深度圖示：較淺上浮、較深下潛、相同巡航。</summary>
        public static Texture2D ForDepthChange(DepthTier from, DepthTier to)
        {
            return to < from ? Ascent : to > from ? Descent : Cruising;
        }
    }
}
