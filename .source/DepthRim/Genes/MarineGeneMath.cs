using System;

namespace DepthRim
{
    /// <summary>
    /// 一個角色的壓力參數（基因設計稿 §4）。沒有耐壓基因的人是 <see cref="Baseline"/>。純值型別，單元測試直接用。
    /// </summary>
    public struct PressureProfile
    {
        /// <summary>適應值和環境可以差幾級而沒有不適（基準 1）。</summary>
        public float tolerance;

        /// <summary>適應速度倍率（基準 1）。</summary>
        public float speed;

        /// <summary>適應值最多到幾級（基準 3 = 海床）。</summary>
        public int maxLevel;

        /// <summary>出艙時水壓傷害的倍率（基準 1）。</summary>
        public float damageFactor;

        public static readonly PressureProfile Baseline = new PressureProfile
        {
            tolerance = PressureAdaptationMath.Tolerance,
            speed = 1f,
            maxLevel = PressureAdaptationMath.MaxLevel,
            damageFactor = 1f,
        };

        /// <summary>任何壓力都不會不適（淵體）。</summary>
        public bool NeverUncomfortable => tolerance >= PressureAdaptationMath.MaxLevel;
    }

    /// <summary>
    /// 一個角色所有海洋基因合起來的效果（<see cref="DepthRimGenes"/> 從基因彙整，純計算用這個值）。
    /// 倍率相乘；容許差、上限取最極端的那一個（同一類基因互斥，實際上只會有一個）。
    /// </summary>
    public struct MarineGeneProfile
    {
        public PressureProfile pressure;

        /// <summary>溺水累積倍率：0 = 不會溺水（鰓）。</summary>
        public float drowningFactor;

        /// <summary>氣瓶耗氣倍率：0 = 不耗氣（鰓）。</summary>
        public float airUseFactor;

        /// <summary>有鰓：離水會累積鰓乾燥。</summary>
        public bool gills;

        /// <summary>不會習慣暈船（易暈船）。敏感度本身是統計 <c>DepthRim_SeasicknessSensitivity</c>，由基因的 statOffsets／statFactors 改。</summary>
        public bool noSeasickAcclimation;

        /// <summary>畏光等級：0 沒有、1 畏光、2 強烈畏光。</summary>
        public int photophobia;

        /// <summary>水壓傷害額外的自然消退倍率（抗減壓病 2）。</summary>
        public float barotraumaRecoveryFactor;

        public static readonly MarineGeneProfile Baseline = new MarineGeneProfile
        {
            pressure = PressureProfile.Baseline,
            drowningFactor = 1f,
            airUseFactor = 1f,
            barotraumaRecoveryFactor = 1f,
        };

        /// <summary>沒入水中也能呼吸（不溺水、不耗氣）。</summary>
        public bool BreathesWater => drowningFactor <= 0f;
    }

    /// <summary>海洋基因的純計算（基因設計稿 §2–§5）。</summary>
    public static class MarineGeneMath
    {
        /// <summary>鰓乾燥：每天累積多少（3 天到「乾燥」的 0.5）。</summary>
        public const float GillDryingPerDay = 0.5f / 3f;

        /// <summary>炎熱時（高於 <see cref="GillHotTemperature"/>）乾燥加快的倍率。</summary>
        public const float GillHotFactor = 1.5f;

        public const float GillHotTemperature = 30f;

        /// <summary>泡在水裡多深才算把鰓弄濕（涉水嚴重度 = 水位；0.4 是及腰）。</summary>
        public const float GillWetLevel = 0.4f;

        /// <summary>鰓乾燥的上限。</summary>
        public const float GillDryingMax = 1f;

        /// <summary>經過 <paramref name="ticks"/> 之後的鰓乾燥嚴重度：濕了就歸零，否則累積（炎熱時加快）。</summary>
        public static float GillDrying(float severity, bool wet, float temperature, int ticks)
        {
            if (wet)
            {
                return 0f;
            }
            float rate = GillDryingPerDay * (temperature > GillHotTemperature ? GillHotFactor : 1f);
            return Math.Min(GillDryingMax, severity + rate * ticks / PressureAdaptationMath.TicksPerDay);
        }

        // ── 畏光（§5）：原版人造光最多到 0.5（GlowGrid.GroundGlowAt 把累積光照夾在 0.5），只有日光與過曝範圍到 1 ──

        /// <summary>畏光：光照高於這個值（日光、過曝範圍、太陽燈）才刺眼。</summary>
        public const float PhotophobiaGlow = 0.51f;

        /// <summary>強烈畏光：一般燈光（0.5）就刺眼。</summary>
        public const float LightBlindGlow = 0.45f;

        /// <summary>低於這個光照算「黑暗」：畏光者舒服、暗處工作者有加成。</summary>
        public const float DarkGlow = 0.3f;

        /// <summary>這個光照對這個畏光等級算不算刺眼。</summary>
        public static bool Glaring(int photophobia, float glow)
        {
            if (photophobia <= 0)
            {
                return false;
            }
            return glow >= (photophobia >= 2 ? LightBlindGlow : PhotophobiaGlow);
        }

        public static bool Dark(float glow)
        {
            return glow < DarkGlow;
        }
    }
}
