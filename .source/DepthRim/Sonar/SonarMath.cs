namespace DepthRim
{
    /// <summary>
    /// 聲納的純規則（可單元測試）。溫躍層是聲波的屏障：觀測方與目標分在溫躍層兩側時，偵測半徑打對折；
    /// 其中一方躲在溫躍層裡時打七五折（設計稿 §6、[DMSE_對接研究.md] §3.2）。
    /// </summary>
    public static class SonarMath
    {
        public const float AcrossLayerFactor = 0.5f;

        public const float InLayerFactor = 0.75f;

        public enum Side
        {
            Above,
            InLayer,
            Below
        }

        public static Side SideOf(DepthTier tier)
        {
            switch (tier)
            {
                case DepthTier.Shallow:
                    return Side.Above;
                case DepthTier.Thermocline:
                    return Side.InLayer;
                default:
                    return Side.Below;
            }
        }

        /// <summary>觀測方在 <paramref name="observer"/>、目標在 <paramref name="target"/> 時，偵測半徑乘上多少。</summary>
        public static float LayerFactor(DepthTier observer, DepthTier target)
        {
            Side a = SideOf(observer);
            Side b = SideOf(target);
            if (a == b)
            {
                return 1f;
            }
            if (a == Side.InLayer || b == Side.InLayer)
            {
                return InLayerFactor;
            }
            return AcrossLayerFactor;
        }
    }
}
