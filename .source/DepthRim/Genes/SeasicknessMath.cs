using System;

namespace DepthRim
{
    /// <summary>
    /// 暈船的純計算（基因設計稿 §3）。嚴重度往「暴露量 × 敏感度 × <see cref="BaseScale"/>」移動，上升慢、下降快。
    ///
    /// <para>平衡：一般人（敏感度 1）在暴風雨（擺動倍率 2.5，暴露 1）到「暈船」（0.6），惡劣海況（2.0）停在「頭暈」（約 0.35）；
    /// 易暈船（敏感度 2）在暴風雨到「嚴重暈船」；海腳（敏感度 0）不暈。</para>
    /// </summary>
    public static class SeasicknessMath
    {
        /// <summary>擺動倍率低於這個值不會暈（平靜海是 1）。</summary>
        public const float SwayThreshold = 1.3f;

        /// <summary>擺動倍率從門檻多這麼多時暴露量到 1（2.5 = 暴風雨）。</summary>
        public const float SwaySpan = 1.2f;

        /// <summary>潛艇上浮／下潛中的暴露量。</summary>
        public const float DivingExposure = 0.5f;

        /// <summary>潛艇在淺海航行中的暴露量（貼近海面，受浪影響）。</summary>
        public const float ShallowVoyageExposure = 0.2f;

        /// <summary>暴露量 1、敏感度 1 時的目標嚴重度。</summary>
        public const float BaseScale = 0.6f;

        /// <summary>每天最多上升多少。</summary>
        public const float RisePerDay = 2f;

        /// <summary>每天最多下降多少。</summary>
        public const float FallPerDay = 4f;

        /// <summary>睡著時上升速度的倍率。</summary>
        public const float AsleepRiseFactor = 0.5f;

        /// <summary>連續暴露多久後習慣（敏感度 × <see cref="AcclimatedFactor"/>）。</summary>
        public const int AcclimateTicks = 3 * PressureAdaptationMath.TicksPerDay;

        /// <summary>離開暴露多久後忘掉習慣。</summary>
        public const int ForgetTicks = 5 * PressureAdaptationMath.TicksPerDay;

        public const float AcclimatedFactor = 0.7f;

        public const float QueasySeverity = 0.15f;

        public const float SeasickSeverity = 0.4f;

        public const float SevereSeverity = 0.75f;

        /// <summary>海面上的暴露量（0–1），由擺動倍率換算。</summary>
        public static float SeaExposure(float swayFactor)
        {
            return Clamp01((swayFactor - SwayThreshold) / SwaySpan);
        }

        /// <summary>航行中潛艇的暴露量（0–1）。</summary>
        public static float VoyageExposure(bool diving, bool shallowUnderway)
        {
            return Clamp01((diving ? DivingExposure : 0f) + (shallowUnderway ? ShallowVoyageExposure : 0f));
        }

        /// <summary>目標嚴重度。</summary>
        public static float Target(float exposure, float sensitivity, bool acclimated)
        {
            if (exposure <= 0f || sensitivity <= 0f)
            {
                return 0f;
            }
            return exposure * sensitivity * BaseScale * (acclimated ? AcclimatedFactor : 1f);
        }

        /// <summary>經過 <paramref name="ticks"/> 之後的嚴重度。</summary>
        public static float Step(float severity, float target, int ticks, bool asleep)
        {
            if (ticks <= 0)
            {
                return severity;
            }
            float days = ticks / (float)PressureAdaptationMath.TicksPerDay;
            if (target > severity)
            {
                return Math.Min(target, severity + RisePerDay * (asleep ? AsleepRiseFactor : 1f) * days);
            }
            return Math.Max(target, severity - FallPerDay * days);
        }

        /// <summary>
        /// 習慣的計時：暴露中累積 <paramref name="exposedTicks"/>、清掉離開的時間；沒有暴露時累積 <paramref name="awayTicks"/>，
        /// 超過 <see cref="ForgetTicks"/> 就把暴露時間歸零。
        /// </summary>
        public static void Track(ref int exposedTicks, ref int awayTicks, bool exposed, int ticks)
        {
            if (exposed)
            {
                exposedTicks += ticks;
                awayTicks = 0;
                return;
            }
            awayTicks += ticks;
            if (awayTicks >= ForgetTicks)
            {
                exposedTicks = 0;
            }
        }

        public static bool Acclimated(int exposedTicks, bool canAcclimate)
        {
            return canAcclimate && exposedTicks >= AcclimateTicks;
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
