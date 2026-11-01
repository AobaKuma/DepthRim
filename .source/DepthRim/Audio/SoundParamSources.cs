using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 環境音的層級開關（二期 P2-11）：目前地圖的渲染層級等於 <see cref="tier"/> 時回傳 1，否則 0。
    /// 四個層級各一個環境音 SoundDef，全部掛在冰洋生態域上，由這個參數只讓對的那一個發聲；
    /// 用渲染層級（含除錯覆寫）而不是遊戲層級，所以除錯切換層級時聲音也跟著換。
    /// </summary>
    public class SoundParamSource_DepthTier : SoundParamSource
    {
        public DepthTier tier;

        public override string Label => "DepthRim depth tier = " + tier;

        public override float ValueFor(Sample samp)
        {
            return ActiveTier() == tier ? 1f : 0f;
        }

        /// <summary>目前地圖的渲染層級；不是水下地圖時回傳 null（四個環境音都不出聲）。</summary>
        public static DepthTier? ActiveTier()
        {
            MapComponent_Flooding flooding = Find.CurrentMap?.Flooding();
            return flooding != null && flooding.Active ? flooding.RenderTier : (DepthTier?)null;
        }
    }

    /// <summary>
    /// 鏡頭是否在乾的密閉艙室裡（二期 P2-11）：1 = 在艙室裡（隔著船殼聽外面的海），0 = 在水中。
    /// 環境音用它把音量壓低，模擬「在潛艇裡聽外面」。
    /// </summary>
    public class SoundParamSource_CameraInCompartment : SoundParamSource
    {
        public override string Label => "DepthRim camera in dry compartment";

        public override float ValueFor(Sample samp)
        {
            Map map = Find.CurrentMap;
            MapComponent_Flooding flooding = map?.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return 0f;
            }
            return 1f - UnderwaterAudio.ListenerWetness(map, flooding);
        }
    }
}
