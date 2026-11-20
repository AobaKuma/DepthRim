namespace DepthRim
{
    /// <summary>下潛／上浮的純規則（可單元測試）。</summary>
    public static class DiveMath
    {
        /// <summary>每換一個層級要多久（遊戲 tick，1 小時）。</summary>
        public const int TicksPerTier = 2500;

        /// <summary>潛艇實際所在的層級：巡航深度，但不會比腳下的海床更深。</summary>
        public static DepthTier Effective(DepthTier cruise, DepthTier seafloor)
        {
            return cruise <= seafloor ? cruise : seafloor;
        }

        public enum DiveBlock
        {
            None,
            AlreadyThere,
            BelowSeafloor,
            NoBallast
        }

        /// <summary>
        /// 能不能把巡航深度設為 <paramref name="target"/>。往上永遠可以；往下不能超過海床，而且船上要有壓載（<paramref name="ballastTanks"/> 座）。
        /// 船殼耐壓不是門檻（§2.2）：超出的話由 <see cref="HullWarning"/> 先確認。
        /// </summary>
        public static DiveBlock CanSet(DepthTier current, DepthTier target, DepthTier seafloor, int ballastTanks)
        {
            if (target == current)
            {
                return DiveBlock.AlreadyThere;
            }
            if (target < current)
            {
                return DiveBlock.None;
            }
            if (target > seafloor)
            {
                return DiveBlock.BelowSeafloor;
            }
            if (ballastTanks <= 0)
            {
                return DiveBlock.NoBallast;
            }
            return DiveBlock.None;
        }

        /// <summary>往目標走一步（一個層級）。</summary>
        public static DepthTier Step(DepthTier current, DepthTier target)
        {
            if (current < target)
            {
                return current + 1;
            }
            if (current > target)
            {
                return current - 1;
            }
            return current;
        }

        /// <summary>從 <paramref name="from"/> 換到 <paramref name="to"/> 總共要幾 tick。</summary>
        public static int TravelTicks(DepthTier from, DepthTier to)
        {
            int steps = from > to ? from - to : to - from;
            return steps * TicksPerTier;
        }
    
        /// <summary>能下錨的海床：淺海，或深海海床（觸底）。變溫層與深海沒有能下錨的底（設計稿 §4.7）。</summary>
        public static bool Anchorable(DepthTier seafloor)
        {
            return seafloor == DepthTier.Shallow || seafloor == DepthTier.Seabed;
        }

        public enum AnchorBlock
        {
            None,
            NoBottom,
            NotAtBottom
        }

        /// <summary>
        /// 能不能在海床層級為 <paramref name="siteSeafloor"/> 的地方下錨：那裡要有能下錨的底，
        /// 潛艇也要正停在那個深度（巡航深度等於海床、沒有在換層）。
        /// </summary>
        public static AnchorBlock CanAnchor(DepthTier cruise, DepthTier target, DepthTier siteSeafloor)
        {
            if (!Anchorable(siteSeafloor))
            {
                return AnchorBlock.NoBottom;
            }
            if (cruise != siteSeafloor || target != cruise)
            {
                return AnchorBlock.NotAtBottom;
            }
            return AnchorBlock.None;
        }
    }
}
