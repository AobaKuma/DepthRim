using Verse;

namespace DepthRim
{
    /// <summary>一張地圖現在會不會把艙外的人沖走。</summary>
    public enum HazardKind
    {
        None,

        /// <summary>潛艇推進中（<see cref="VoyageOverboard.Underway"/>）：艙外沒扣繩的人一定失蹤。</summary>
        Voyage,

        /// <summary>海面地圖的水面層遇上惡劣海況：艙外沒扣繩的人有機率被浪捲走。</summary>
        RoughSea,
    }

    /// <summary>落海風險的判定（§3）。</summary>
    public static class OverboardHazard
    {
        /// <summary>測試用：不是 null 時取代水面層的天氣倍率（原版每個 tick 重算風速，測試無法固定）。</summary>
        public static float? debugSeaFactor;

        public static HazardKind HazardOf(Map map)
        {
            if (map == null)
            {
                return HazardKind.None;
            }
            if (map.Parent is WorldObject_Submarine sub)
            {
                return VoyageOverboard.Underway(sub) ? HazardKind.Voyage : HazardKind.None;
            }
            if (SeaLevels.IsSeaLevel(map) && RopeTetherMath.IsRoughSea(SeaFactor(map)))
            {
                return HazardKind.RoughSea;
            }
            return HazardKind.None;
        }

        /// <summary>
        /// 水面層的天氣倍率：用風速算出的目標值（<see cref="MapComponent_SeaSurface.TargetSwayFactor"/>），
        /// 不用畫面上按真實時間平滑過的 <see cref="MapComponent_SeaSurface.SwayFactor"/>。
        /// </summary>
        public static float SeaFactor(Map map)
        {
            if (debugSeaFactor.HasValue)
            {
                return debugSeaFactor.Value;
            }
            MapComponent_SeaSurface sea = map.GetComponent<MapComponent_SeaSurface>();
            return sea != null ? sea.TargetSwayFactor() : SeaSway.WeatherFactor(map.windManager?.WindSpeed ?? 0f);
        }
    }
}
