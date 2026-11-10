namespace DepthRim
{
    /// <summary>
    /// 潛艇航行的推進規則（和 DMSE 的轉移飛行不同）：航程只有在推進器實際運作時才前進。
    /// DMSE 的進度完全由時間決定（起飛時刻 + 點火／滑行時長），這裡在推力不足時把時鐘往後推，
    /// 等於這段時間沒有前進：推力剩一半就以一半的速度前進，推力為零就停在海中。純函式，可單元測試。
    /// </summary>
    public static class VoyageMath
    {
        /// <summary>多久重新確認一次推進器狀態（ticks）。</summary>
        public const int ThrustCheckInterval = 60;

        /// <summary>目前推力相對於出航時推力的比例（0..1）。出航時沒有推力（不該發生）就當作全速，避免永遠卡住。</summary>
        public static float ThrustFraction(float current, float planned)
        {
            if (planned <= 0f)
            {
                return 1f;
            }
            float f = current / planned;
            return f < 0f ? 0f : f > 1f ? 1f : f;
        }

        /// <summary>
        /// 這 <paramref name="delta"/> ticks 裡有多少 ticks「沒有前進」：delta × (1 − 比例)，小數累積在 <paramref name="carry"/>。
        /// 回傳要把航程時鐘往後推的整數 ticks。
        /// </summary>
        public static int StallTicks(int delta, float fraction, ref float carry)
        {
            if (delta <= 0)
            {
                return 0;
            }
            carry += delta * (1f - fraction);
            int whole = (int)carry;
            carry -= whole;
            return whole;
        }

        // ── 航程耗電（和 DMSE 不同：出航不預扣燃料，推進器在航行中耗電就是全部成本）──

        /// <summary>沒有在航行時推進器的待機耗電比例。</summary>
        public const float StandbyFactor = 0.1f;

        /// <summary><paramref name="watts"/> W 持續 <paramref name="hours"/> 小時要多少 Wd（1 W 一天 = 1 Wd）。</summary>
        public static float EnergyWd(float watts, float hours)
        {
            return watts <= 0f || hours <= 0f ? 0f : watts * hours / 24f;
        }

        /// <summary>
        /// 儲電 <paramref name="storedWd"/> 能讓 <paramref name="watts"/> W 的推進器走幾格：
        /// 先扣掉固定時間（出發、抵達點火），其餘每格 <paramref name="hoursPerTile"/> 小時。
        /// 連固定時間都撐不過回傳 -1（DMSE 顯示「無法出發」）；沒有耗電回傳 <see cref="int.MaxValue"/>。
        /// </summary>
        public static int RangeTiles(float storedWd, float watts, float fixedHours, float hoursPerTile)
        {
            if (watts <= 0f || hoursPerTile <= 0f)
            {
                return int.MaxValue;
            }
            float hours = storedWd * 24f / watts - fixedHours;
            if (hours < 0f)
            {
                return -1;
            }
            double tiles = System.Math.Floor(hours / hoursPerTile);
            return tiles >= int.MaxValue ? int.MaxValue : (int)tiles;
        }
    }
}
