using UnityEngine;

namespace DepthRim
{
    /// <summary>原版氣候事件與冷暖洋流對箱網的「額外」修正（§5.5）。熱浪寒流的水溫已在戶外溫度裡，這裡不加水溫。</summary>
    public struct ClimateEffect
    {
        public float oxygen;

        public float growth;

        public float bloomMtbFactor;

        public static ClimateEffect None => new ClimateEffect { oxygen = 0f, growth = 1f, bloomMtbFactor = 1f };

        public void Add(ClimateEffect other)
        {
            oxygen += other.oxygen;
            growth *= other.growth;
            bloomMtbFactor *= other.bloomMtbFactor;
        }
    }

    public enum OceanCurrentKind
    {
        None,
        Warm,
        Cold
    }

    /// <summary>純函式：原版氣候事件與洋流事件的修正。</summary>
    public static class ClimateMath
    {
        public static ClimateEffect HeatWave => new ClimateEffect { oxygen = -0.10f, growth = 1f, bloomMtbFactor = 0.6f };

        public static ClimateEffect ColdSnap => new ClimateEffect { oxygen = 0.05f, growth = 1f, bloomMtbFactor = 1.4f };

        public static ClimateEffect VolcanicWinter => new ClimateEffect { oxygen = 0f, growth = 0.85f, bloomMtbFactor = 1.2f };

        public static ClimateEffect ToxicFallout => new ClimateEffect { oxygen = -0.05f, growth = 0.85f, bloomMtbFactor = 0.8f };

        /// <summary>原版氣候事件的合計修正（可同時有好幾個）。</summary>
        public static ClimateEffect Vanilla(bool heatWave, bool coldSnap, bool volcanicWinter, bool toxicFallout)
        {
            ClimateEffect e = ClimateEffect.None;
            if (heatWave) { e.Add(HeatWave); }
            if (coldSnap) { e.Add(ColdSnap); }
            if (volcanicWinter) { e.Add(VolcanicWinter); }
            if (toxicFallout) { e.Add(ToxicFallout); }
            return e;
        }

        // ── 冷暖洋流 ──
        public const float CurrentSurfaceOffset = 3f;
        public const float CurrentMidwaterOffset = 1.4f;
        public const float CurrentEventFactor = 1.15f;
        public const float WarmWaterMidpoint = 18f;
        public const float ColdWaterMidpoint = 8f;
        public const float CurrentGrowthBonus = 1.1f;

        /// <summary>洋流在某深度的水溫偏移：10 m ±3、150 m ±1.4、500 m（變溫層以下）0，中間線性內插。灣內不套用。</summary>
        public static float CurrentOffset(OceanCurrentKind kind, float netDepth, CageVenue venue)
        {
            if (kind == OceanCurrentKind.None || venue == CageVenue.Bay)
            {
                return 0f;
            }
            float magnitude;
            if (netDepth <= 10f)
            {
                magnitude = CurrentSurfaceOffset;
            }
            else if (netDepth <= 150f)
            {
                magnitude = Mathf.Lerp(CurrentSurfaceOffset, CurrentMidwaterOffset, (netDepth - 10f) / 140f);
            }
            else if (netDepth < 500f)
            {
                magnitude = Mathf.Lerp(CurrentMidwaterOffset, 0f, (netDepth - 150f) / 350f);
            }
            else
            {
                magnitude = 0f;
            }
            return kind == OceanCurrentKind.Warm ? magnitude : -magnitude;
        }

        /// <summary>洋流的溶氧／藻華修正（不含水溫）。灣內不套用。</summary>
        public static ClimateEffect Current(OceanCurrentKind kind, CageVenue venue)
        {
            if (kind == OceanCurrentKind.None || venue == CageVenue.Bay)
            {
                return ClimateEffect.None;
            }
            return kind == OceanCurrentKind.Warm
                ? new ClimateEffect { oxygen = -0.05f, growth = 1f, bloomMtbFactor = 0.8f }
                : new ClimateEffect { oxygen = 0.05f, growth = 1f, bloomMtbFactor = 1.2f };
        }

        public static bool IsWarmWaterFish(float tempMin, float tempMax)
        {
            return (tempMin + tempMax) * 0.5f >= WarmWaterMidpoint;
        }

        public static bool IsColdWaterFish(float tempMin, float tempMax)
        {
            return (tempMin + tempMax) * 0.5f <= ColdWaterMidpoint;
        }

        /// <summary>暖流讓暖水魚、寒流讓冷水魚長快 10%。</summary>
        public static float CurrentGrowth(OceanCurrentKind kind, CageVenue venue, float tempMin, float tempMax)
        {
            if (venue == CageVenue.Bay)
            {
                return 1f;
            }
            if (kind == OceanCurrentKind.Warm && IsWarmWaterFish(tempMin, tempMax))
            {
                return CurrentGrowthBonus;
            }
            if (kind == OceanCurrentKind.Cold && IsColdWaterFish(tempMin, tempMax))
            {
                return CurrentGrowthBonus;
            }
            return 1f;
        }

        /// <summary>洋流發電機的強度倍率：有洋流事件 1.15，否則 1。</summary>
        public static float EventFactor(OceanCurrentKind kind)
        {
            return kind == OceanCurrentKind.None ? 1f : CurrentEventFactor;
        }
    }
}
