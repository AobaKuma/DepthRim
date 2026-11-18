using UnityEngine;

namespace DepthRim
{
    /// <summary>
    /// 玩家突擊艇的燃料規則（純計算，單元測試直接驗）。出發前要有來回的燃料，出發時燒掉去程，回程時再燒回程；
    /// 一滿箱（80 化學燃料）剛好來回 8 格海路，等於艇的航程上限。
    /// </summary>
    public static class BoardingCraftFuel
    {
        /// <summary>每格海路燒多少燃料。</summary>
        public const float PerTile = 5f;

        public static float OneWay(int distance) => Mathf.Max(0, distance) * PerTile;

        public static float RoundTrip(int distance) => OneWay(distance) * 2f;

        public static bool CanReach(float fuel, int distance) => fuel + 0.001f >= RoundTrip(distance);

        /// <summary>現有燃料最遠能來回幾格。</summary>
        public static int RoundTripTiles(float fuel) => Mathf.FloorToInt((fuel + 0.001f) / (PerTile * 2f));
    }
}
