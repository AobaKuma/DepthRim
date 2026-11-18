using System;

namespace DepthRim
{
    /// <summary>
    /// 水中內爆的純數學（實作計畫 Docs/水中內爆_實作計畫.md §2、§4.5）：判定、氣泡曲線、依水深縮放、粒子數、音量與鏡頭震動。
    /// 不碰遊戲狀態，單元測試直接呼叫。
    /// </summary>
    public static class ImplosionMath
    {
        /// <summary>爆炸中心格水位達到這條線才算「水中」：和站著被淹沒同一條線（2026-11-18 決定）。</summary>
        public const float MinLevel = FloodingMath.SubmergedLevelStanding;

        /// <summary>主氣泡膨脹段佔第一次脈動的比例：之後被水壓擠回中心。</summary>
        public const float ExpandFraction = 0.64f;

        /// <summary>反彈氣泡的持續時間（相對第一次脈動）。</summary>
        public const float ReboundDurationFactor = 0.55f;

        /// <summary>崩塌時鏡頭震動比原版重一些：水中衝擊比較「實」。</summary>
        public const float ShakeFactor = 1.3f;

        /// <summary>同時播放主氣泡的上限；超過的只放簡化效果，避免轟炸時掉幀。</summary>
        public const int MaxActive = 12;

        /// <summary>
        /// 這個爆炸要不要接手成內爆。
        /// <paramref name="callerWantsVisuals"/> 為 false 表示呼叫端本來就不要特效（技能、腳本爆炸），不接手。
        /// </summary>
        public static bool ShouldImplode(bool exemptDamage, bool callerWantsVisuals, bool hasWater, bool openWater, float level)
        {
            if (exemptDamage || !callerWantsVisuals || !hasWater)
            {
                return false;
            }
            return openWater || level >= MinLevel;
        }

        /// <summary>火焰類爆炸在水中要不要改成燙傷：Flame 本身或用火焰 worker 的 DamageDef，除非標記保留。</summary>
        public static bool ConvertsToScald(bool isFlameDef, bool hasFlameWorker, bool keepDamageType, bool alreadyScald)
        {
            return !keepDamageType && !alreadyScald && (isFlameDef || hasFlameWorker);
        }

        /// <summary>
        /// 相對水壓。真實的氣泡最大半徑 ∝ P^(-1/3)、週期 ∝ P^(-5/6)；這裡的壓力值與 §4.5 的指數都刻意放緩，
        /// 讓深海的內爆仍然看得見，只是更小、更快。
        /// </summary>
        public static float PressureOf(DepthTier tier)
        {
            switch (tier)
            {
                case DepthTier.Thermocline: return 1.6f;
                case DepthTier.Deep: return 2.5f;
                case DepthTier.Seabed: return 4f;
                default: return 1f;
            }
        }

        /// <summary>爆炸能量（無單位）：由傷害量與半徑推，讓手榴彈、迫擊砲、魚雷大小有別。</summary>
        public static float Energy(int damAmount, float radius)
        {
            return Math.Max(1f, Math.Max(0, damAmount) * Math.Max(0f, radius));
        }

        /// <summary>主氣泡最大半徑（格）。</summary>
        public static float MaxBubbleRadius(float explosionRadius, DepthTier tier)
        {
            return Math.Max(0.6f, explosionRadius * 0.9f * (float)Math.Pow(PressureOf(tier), -1.0 / 3.0));
        }

        /// <summary>第一次脈動（膨脹到崩塌）的秒數。</summary>
        public static float Period(float energy, DepthTier tier)
        {
            float period = 0.104f * (float)Math.Pow(Math.Max(1f, energy), 1.0 / 3.0) * (float)Math.Pow(PressureOf(tier), -0.5);
            return Clamp(period, 0.25f, 1.2f);
        }

        /// <summary>反彈氣泡相對最大半徑的大小：越深能量損失越大。</summary>
        public static float ReboundRatio(DepthTier tier)
        {
            switch (tier)
            {
                case DepthTier.Thermocline: return 0.38f;
                case DepthTier.Deep: return 0.31f;
                case DepthTier.Seabed: return 0.25f;
                default: return 0.45f;
            }
        }

        /// <summary>整段主氣泡（含反彈）的秒數。</summary>
        public static float TotalBubbleSeconds(float period)
        {
            return period * (1f + ReboundDurationFactor);
        }

        /// <summary>
        /// 主氣泡此刻的半徑比例（0–1，乘上 <see cref="MaxBubbleRadius"/>）：
        /// 膨脹（減速）→ 崩塌（加速）到 0 → 反彈到 <paramref name="rebound"/> → 再塌回 0。
        /// </summary>
        public static float BubbleScale(float seconds, float period, float rebound)
        {
            if (seconds <= 0f || period <= 0f)
            {
                return 0f;
            }
            float peak = period * ExpandFraction;
            if (seconds <= peak)
            {
                float t = seconds / peak;
                return 1f - (1f - t) * (1f - t);
            }
            if (seconds <= period)
            {
                float t = (seconds - peak) / (period - peak);
                return 1f - t * t;
            }
            float reboundLength = period * ReboundDurationFactor;
            float r = (seconds - period) / reboundLength;
            if (r >= 1f)
            {
                return 0f;
            }
            return rebound * (float)Math.Sin(Math.PI * r);
        }

        /// <summary>泡沫團在第一次脈動的這個比例開始出現：氣泡塌成一團泡沫（參考影片 1:06–1:10）。</summary>
        public const float FoamStartFraction = 0.85f;

        /// <summary>
        /// 主氣泡的不透明度：膨脹時是半透明的氣體球；接近崩塌時讓位給泡沫團（只剩三成）；反彈時只剩淡淡的氣體。
        /// </summary>
        public static float BubbleAlpha(float seconds, float period)
        {
            if (seconds <= 0f || period <= 0f || seconds >= TotalBubbleSeconds(period))
            {
                return 0f;
            }
            if (seconds > period)
            {
                return 0.35f;
            }
            float handOff = Clamp((seconds - period * FoamStartFraction) / (period * (1f - FoamStartFraction)), 0f, 1f);
            return 0.9f * (1f - 0.7f * handOff);
        }

        /// <summary>
        /// 泡沫團的大小（相對主氣泡最大半徑）：崩塌前一刻出現並長到 0.45，之後在氣泡柱期間慢慢膨脹到 0.7。
        /// </summary>
        public static float FoamScale(float seconds, float period, float columnSeconds)
        {
            float start = period * FoamStartFraction;
            if (period <= 0f || seconds <= start)
            {
                return 0f;
            }
            if (seconds <= period)
            {
                return 0.45f * (seconds - start) / (period - start);
            }
            return 0.45f + 0.25f * Clamp((seconds - period) / Math.Max(0.01f, columnSeconds), 0f, 1f);
        }

        /// <summary>泡沫團的不透明度：崩塌時最實，維持一半的氣泡柱時間，然後慢慢散掉。</summary>
        public static float FoamAlpha(float seconds, float period, float columnSeconds)
        {
            float start = period * FoamStartFraction;
            if (period <= 0f || seconds <= start)
            {
                return 0f;
            }
            if (seconds <= period)
            {
                return 0.9f * (seconds - start) / (period - start);
            }
            float t = (seconds - period) / Math.Max(0.01f, columnSeconds);
            if (t >= 1f)
            {
                return 0f;
            }
            return t <= 0.5f ? 0.9f : 0.9f * (1f - (t - 0.5f) / 0.5f);
        }

        /// <summary>泡沫團崩塌後往上漂的距離（格，畫面上方）。</summary>
        public static float FoamRise(float seconds, float period)
        {
            return 0.35f * Math.Max(0f, seconds - period);
        }

        /// <summary>震波環從中心擴張到最大的秒數：比氣泡快很多（壓力波先到）。</summary>
        public static float RingSeconds(float period)
        {
            return Math.Max(0.12f, period * 0.35f);
        }

        /// <summary>震波環半徑（格）：減速擴張到爆炸半徑的 1.6 倍。</summary>
        public static float RingRadius(float seconds, float period, float explosionRadius)
        {
            float duration = RingSeconds(period);
            if (seconds <= 0f)
            {
                return 0f;
            }
            float t = Math.Min(1f, seconds / duration);
            return explosionRadius * 1.6f * (1f - (1f - t) * (1f - t));
        }

        /// <summary>震波環不透明度：一出現最亮，擴張到底時消失。</summary>
        public static float RingAlpha(float seconds, float period)
        {
            float duration = RingSeconds(period);
            if (seconds <= 0f || seconds >= duration)
            {
                return 0f;
            }
            float t = seconds / duration;
            return (1f - t) * (1f - t);
        }

        /// <summary>
        /// 依所在處的水中光照（<see cref="WaterLight.At"/>，0–1）調暗：氣泡本身不發光，暗水裡只剩隱約的反光。
        /// 留 0.45 的底，玩家在全黑的深海仍看得出發生了內爆。
        /// </summary>
        public static float LightFactor(float light)
        {
            return 0.45f + 0.55f * Clamp(light, 0f, 1f);
        }

        /// <summary>
        /// 氣泡表面的晃動（0 附近的小數，畫的時候一軸 ×(1+w)、另一軸 ×(1−w)）：真的爆炸氣泡不是正圓，崩塌時最不穩。
        /// <paramref name="seed"/> 讓每個實例的相位不同。
        /// </summary>
        public static float Wobble(float seconds, float period, float seed)
        {
            if (period <= 0f)
            {
                return 0f;
            }
            float amplitude = 0.035f + 0.07f * RimBrightness(seconds, period);
            return amplitude * (float)Math.Sin(2.0 * Math.PI * 2.5 * seconds / period + seed);
        }

        /// <summary>崩塌瞬間邊緣最亮：0–1。</summary>
        public static float RimBrightness(float seconds, float period)
        {
            if (period <= 0f)
            {
                return 0f;
            }
            float d = Math.Abs(seconds - period) / (period * 0.25f);
            return Clamp(1f - d, 0f, 1f);
        }

        /// <summary>整個實例最多丟幾顆粒子。</summary>
        public static int MaxParticles(float radius)
        {
            int n = (int)Math.Round(Math.PI * radius * radius * 0.6);
            return Math.Max(6, Math.Min(60, n));
        }

        /// <summary>每格丟粒子的機率：大爆炸的格子多，單格機率降低，總數不爆量。</summary>
        public static float CellParticleChance(float radius)
        {
            float cells = (float)(Math.PI * radius * radius);
            return cells <= 0f ? 0f : Clamp(MaxParticles(radius) / cells, 0f, 1f);
        }

        /// <summary>往中心吸的粒子還有多久要抵達（秒）：到崩塌為止，至少留一點時間讓牠看得見。</summary>
        public static float SecondsToCollapse(float ageSeconds, float period)
        {
            return Math.Max(0.08f, period - ageSeconds);
        }

        /// <summary>往中心吸的速度（格／秒）：崩塌時剛好抵達中心。</summary>
        public static float InwardSpeed(float distance, float ageSeconds, float period)
        {
            return Math.Max(0f, distance) / SecondsToCollapse(ageSeconds, period);
        }

        /// <summary>崩塌後氣泡柱持續的秒數：燙傷型（水被煮沸）更久。</summary>
        public static float ColumnSeconds(bool scald)
        {
            return scald ? 2.6f : 1.6f;
        }

        /// <summary>氣泡柱每隔幾 tick 冒一顆：燙傷型更密；簡化版（同時太多內爆時）減半。</summary>
        public static int ColumnIntervalTicks(bool scald, bool simplified)
        {
            int interval = scald ? 3 : 5;
            return simplified ? interval * 2 : interval;
        }

        /// <summary>崩塌時揚起幾團混濁水團。</summary>
        public static int MurkCount(float radius)
        {
            int n = (int)Math.Round(radius * 1.5f);
            return Math.Max(2, Math.Min(8, n));
        }

        public static float VolumeFactor(float radius)
        {
            return Clamp(0.6f + radius * 0.15f, 0.6f, 1.4f);
        }

        /// <summary>越大、越深越低沉。</summary>
        public static float PitchFactor(float radius, DepthTier tier)
        {
            return Clamp(1.15f - radius * 0.05f - (int)tier * 0.04f, 0.7f, 1.15f);
        }

        /// <summary>崩塌時的鏡頭震動：原版公式 4 × 半徑 × 倍率 ÷ 距離，再乘 <see cref="ShakeFactor"/>。</summary>
        public static float Shake(float radius, float screenShakeFactor, float cameraDistance, float settingFactor = 1f)
        {
            if (cameraDistance <= 0f)
            {
                return 0f;
            }
            return 4f * radius * screenShakeFactor * ShakeFactor * settingFactor / cameraDistance;
        }

        private static float Clamp(float v, float min, float max)
        {
            return v < min ? min : v > max ? max : v;
        }
    }
}
