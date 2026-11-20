using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 洋流發電機的數值（純函式，單元測試直接跑）：洋流強度、水流通道的阻塞、輸出功率。
    ///
    /// <para>比照原版風力發電機（<c>RimWorld.CompPowerPlantWind</c>）的算法：輸出 = 額定功率 × 風（流）強度 × 通道阻塞折減，
    /// 強度上限 1.4 倍（原版是 1.5 倍），每 250 ticks 重算一次。差別在「風速」換成「洋流」：</para>
    /// <list type="bullet">
    /// <item>隨深度遞減：淺海最強，海床最弱（<see cref="TierBase"/>）；</item>
    /// <item>天氣只影響表層，越深越不明顯（<see cref="WeatherShare"/>，暴風時表層最多 1.35 倍）；</item>
    /// <item>再加上緩慢的潮汐漲落（<see cref="Surge"/>，約 83 秒一個週期，0.85–1.15 倍）。</item>
    /// </list>
    /// </summary>
    public static class OceanCurrentMath
    {
        /// <summary>洋流強度的上限（和原版風力發電機的 1.5 倍同一個用途，但洋流比較穩定，不用到 1.5）。</summary>
        public const float MaxStrength = 1.4f;

        /// <summary>潮汐漲落的週期（ticks）。</summary>
        public const long SurgePeriod = 5000;

        /// <summary>洋流漲落的幅度：<see cref="Surge"/> 在 1 ± 這個值之間。</summary>
        public const float SurgeAmplitude = 0.15f;

        /// <summary>天氣對錶層洋流的影響上限（暴風時 1.35 倍）。</summary>
        public const float MaxWeatherFactor = 1.35f;

        /// <summary>每個被擋住的通道格子減少多少輸出（比照原版的 <c>PowerReductionPercentPerObstacle</c>）。</summary>
        public const float PowerReductionPercentPerObstacle = 0.2f;

        /// <summary>通道全被擋住時仍保留的輸出比例：水流再差也不會完全停擺。</summary>
        public const float MinFlowFactor = 0.2f;

        /// <summary>相鄰的水流通道（建築前後各 <see cref="DefaultFlowReach"/> 格）預設幾格。</summary>
        public const int DefaultFlowReach = 2;

        /// <summary>這個深度層級的水流強度基準：淺海 1、變溫層 0.85、深海 0.7、海床 0.55。</summary>
        public static float TierBase(DepthTier tier)
        {
            switch (tier)
            {
                case DepthTier.Shallow: return 1f;
                case DepthTier.Thermocline: return 0.85f;
                case DepthTier.Deep: return 0.7f;
                case DepthTier.Seabed: return 0.55f;
                default: return 1f;
            }
        }

        /// <summary>天氣（表層風浪）能影響到多少：淺海 0.8、變溫層 0.5、深海 0.3、海床 0.15。</summary>
        public static float WeatherShare(DepthTier tier)
        {
            switch (tier)
            {
                case DepthTier.Shallow: return 0.8f;
                case DepthTier.Thermocline: return 0.5f;
                case DepthTier.Deep: return 0.3f;
                case DepthTier.Seabed: return 0.15f;
                default: return 0f;
            }
        }

        /// <summary>風速換成天氣倍率（1–1.35）：風速 0.5 以下算平靜，1.5 以上最強。</summary>
        public static float WeatherFactor(float windSpeed)
        {
            return 1f + (MaxWeatherFactor - 1f) * Mathf.Clamp01((windSpeed - 0.5f) / 1.5f);
        }

        /// <summary>潮汐漲落：時間 <paramref name="ticks"/>、相位 <paramref name="phase"/>（0–1）時的倍率（0.85–1.15）。</summary>
        public static float Surge(long ticks, float phase)
        {
            float t = (ticks % SurgePeriod) / (float)SurgePeriod + phase;
            return 1f + SurgeAmplitude * Mathf.Sin(Mathf.PI * 2f * t);
        }

        /// <summary>每台發電機的相位（由 thingIDNumber 決定）：相鄰的幾台不會同步漲落。</summary>
        public static float Phase(int thingIDNumber)
        {
            return (thingIDNumber * 0.6180339887f) % 1f;
        }

        /// <summary>
        /// 當下的洋流強度：深度基準 × （天氣只有表層那部分有影響）× 潮汐漲落，上限 <paramref name="maxStrength"/>。
        /// </summary>
        public static float Strength(DepthTier tier, float windSpeed, long ticks, float phase, float maxStrength = MaxStrength)
        {
            float weather = Mathf.Lerp(1f, WeatherFactor(windSpeed), WeatherShare(tier));
            return Mathf.Min(maxStrength, TierBase(tier) * weather * Surge(ticks, phase));
        }

        /// <summary>通道阻塞的輸出倍率：每格 −20%，最低保留 <see cref="MinFlowFactor"/>。</summary>
        public static float FlowFactor(int blockedCells, float reductionPerObstacle = PowerReductionPercentPerObstacle, float min = MinFlowFactor)
        {
            if (blockedCells <= 0)
            {
                return 1f;
            }
            return Mathf.Max(min, 1f - reductionPerObstacle * blockedCells);
        }

        /// <summary>輸出功率（W，正 = 發電）：額定功率 × 洋流強度 × 通道倍率。</summary>
        public static float Output(float basePowerConsumption, float strength, int blockedCells)
        {
            if (basePowerConsumption >= 0f)
            {
                return 0f;
            }
            return (0f - basePowerConsumption) * strength * FlowFactor(blockedCells);
        }

        /// <summary>
        /// 水流通道：建築本體（<paramref name="size"/>，中心 <paramref name="center"/>、方向 <paramref name="rot"/>）前後各
        /// <paramref name="reach"/> 格、寬度同建築的兩塊矩形，不含建築自己佔的格子。水流沿建築的朝向進出（和原版風力發電機的風道同一個概念）。
        /// </summary>
        public static IEnumerable<IntVec3> FlowCells(IntVec3 center, Rot4 rot, IntVec2 size, int reach)
        {
            if (reach < 0)
            {
                reach = 0;
            }
            CellRect body = GenAdj.OccupiedRect(center, rot, size);
            CellRect corridor = rot == Rot4.North || rot == Rot4.South
                ? new CellRect(body.minX, body.minZ - reach, body.Width, body.Height + 2 * reach)
                : new CellRect(body.minX - reach, body.minZ, body.Width + 2 * reach, body.Height);
            foreach (IntVec3 c in corridor)
            {
                if (!body.Contains(c))
                {
                    yield return c;
                }
            }
        }
    }
}
