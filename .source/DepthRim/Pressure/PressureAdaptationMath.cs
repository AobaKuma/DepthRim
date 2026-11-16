using System;

namespace DepthRim
{
    /// <summary>
    /// 壓力適應的純計算（不碰遊戲物件，單元測試直接驗）。
    ///
    /// <para>每個血肉角色有一個「適應值」，單位和環境壓力等級相同（0 = 常壓／淺海，1 = 變溫層，2 = 深海，3 = 海床）。
    /// 適應值一直往所在環境的壓力等級移動，每移動 1 級要 <see cref="MinDaysPerLevel"/>～<see cref="MaxDaysPerLevel"/> 天，
    /// 由血液過濾能力決定（2026-11-15：原本是呼吸能力、6 天）。適應值和環境差不超過 <see cref="Tolerance"/> 級時沒有任何影響；超過的部分就是「壓力不適」的嚴重度，
    /// 下潛和上浮都算。</para>
    /// </summary>
    public static class PressureAdaptationMath
    {
        /// <summary>適應值和環境可以相差幾級而沒有不適。</summary>
        public const float Tolerance = 1f;

        /// <summary>血液過濾能力 100% 時，適應 1 級要幾天。</summary>
        public const float BaseDaysPerLevel = 1f;

        public const float MinDaysPerLevel = 0.5f;

        public const float MaxDaysPerLevel = 2f;

        public const int TicksPerDay = 60000;

        /// <summary>壓力等級的上限（海床）。</summary>
        public const int MaxLevel = 3;

        /// <summary>適應 1 級要幾天：1 ÷ 血液過濾能力，限制在 0.5～2 天（過濾 200% 以上半天、100% 是 1 天、50% 以下 2 天）。</summary>
        public static float DaysPerLevel(float filtration)
        {
            if (filtration <= 0f || float.IsNaN(filtration))
            {
                return MaxDaysPerLevel;
            }
            return Math.Min(MaxDaysPerLevel, Math.Max(MinDaysPerLevel, BaseDaysPerLevel / filtration));
        }

        /// <summary>經過 <paramref name="ticks"/> 之後的適應值：往環境壓力移動，不會超過。</summary>
        public static float Step(float adapted, float ambient, float filtration, int ticks)
        {
            if (ticks <= 0)
            {
                return adapted;
            }
            float move = ticks / (DaysPerLevel(filtration) * TicksPerDay);
            float gap = ambient - adapted;
            if (Math.Abs(gap) <= move)
            {
                return ambient;
            }
            return adapted + Math.Sign(gap) * move;
        }

        /// <summary>壓力不適的嚴重度：適應值和環境的差距超過容許範圍的部分（0 = 沒有不適，最多 2）。</summary>
        public static float Mismatch(float adapted, float ambient)
        {
            return Math.Max(0f, Math.Abs(ambient - adapted) - Tolerance);
        }

        /// <summary>還要多久才不會不適（ticks）；已經沒有不適時為 0。</summary>
        public static int TicksUntilComfortable(float adapted, float ambient, float filtration)
        {
            return (int)Math.Ceiling(Mismatch(adapted, ambient) * DaysPerLevel(filtration) * TicksPerDay);
        }

        /// <summary>
        /// 水密門的壓差通行規則：門另一側的壓力會讓這個人比現在更不適時，他不會自己走過去（徵召中照命令走）。
        /// 已經不適的人可以往「比較不糟」的一側走，所以不會被關在外面。
        /// </summary>
        public static bool DoorAllows(bool drafted, float adapted, float here, float there)
        {
            return drafted || Mismatch(adapted, there) <= Mismatch(adapted, here) + 0.001f;
        }

        /// <summary>
        /// 壓力等級換成牆體水壓用的絕對壓力（常壓 0、淺海 1、變溫層 2、深海 4、海床 8，和 <see cref="FloodingMath.PressureFactor"/> 同單位）：
        /// 0 級是常壓（0），1–3 級是 2 的次方。艙壓剛離開 0 就至少是淺海的壓力。
        /// </summary>
        public static float GaugeOfLevel(float level)
        {
            return level <= 0.001f ? 0f : (float)Math.Pow(2.0, level);
        }

        /// <summary><see cref="GaugeOfLevel"/> 的反函數：淺海以下（≤ 1）都算 0 級。</summary>
        public static float LevelOfGauge(float gauge)
        {
            return gauge <= 1f ? 0f : (float)Math.Min(MaxLevel, Math.Log(gauge, 2.0));
        }

        /// <summary>撐不住壓力的艙室（一般的門、地板不是耐壓甲板）每小時洩幾級。</summary>
        public const float UnheldLeakPerHour = 0.5f;

        /// <summary>加壓艙的門每開一次，艙壓往門外的壓力移動幾級。</summary>
        public const float DoorOpeningLeak = 0.1f;

        /// <summary>門一直開著時，艙壓每小時往門外的壓力移動幾級（比加壓機快，開著門加不了壓）。</summary>
        public const float OpenDoorLeakPerHour = 2f;

        /// <summary>加壓艙的壓力往目標移動 <paramref name="maxDelta"/>，不會超過。</summary>
        public static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Math.Abs(target - current) <= maxDelta)
            {
                return target;
            }
            return current + Math.Sign(target - current) * maxDelta;
        }
    }
}
