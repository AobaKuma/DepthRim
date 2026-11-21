using UnityEngine;

namespace DepthRim
{
    /// <summary>
    /// 箱網病害（§6.4）：只有一種病、只會擴散，不直接扣成長或死亡。純函式。
    /// </summary>
    public static class DiseaseSpread
    {
        /// <summary>測試用：擴散與發生判定一定成立。</summary>
        public static bool debugAlwaysInfect;

        public const float NeighborRadius = 12f;
        public const float OutbreakMtbDays = 40f;
        public const float BayOutbreakFactor = 0.8f;
        public const float StressFactor = 0.7f;
        public const float StartSeverity = 0.3f;
        public const float DailyRise = 0.03f;
        public const int SpreadIntervalDays = 3;
        public const float SpreadBase = 0.15f;
        public const float BayVenueFactor = 1.3f;
        public const float TreatReduction = 0.4f;
        public const float HerbalFactor = 0.5f;
        public const int SelfHealDays = 10;
        public const float SelfHealPerDay = 0.02f;
        public const int MedicineCount = 10;

        /// <summary>發生的平均間隔（天）：灣內 ×0.8；暖水（&gt;18 °C）、低氧（&lt;0.5）、高密度（&gt;0.9）各 ×0.7；抗性越高越久。</summary>
        public static float OutbreakMtb(CageVenue venue, float waterTemp, float oxygen, float density, float resistance)
        {
            float mtb = OutbreakMtbDays * (venue == CageVenue.Bay ? BayOutbreakFactor : 1f);
            if (waterTemp > 18f) { mtb *= StressFactor; }
            if (oxygen < 0.5f) { mtb *= StressFactor; }
            if (density > 0.9f) { mtb *= StressFactor; }
            return mtb * Mathf.Max(0.1f, resistance);
        }

        /// <summary>擁擠：min(2, 1 + 0.25 × (鄰居數 − 1))；沒有鄰居是 0（不會擴散）。</summary>
        public static float Crowding(int neighbors)
        {
            if (neighbors <= 0)
            {
                return 0f;
            }
            return Mathf.Min(2f, 1f + 0.25f * (neighbors - 1));
        }

        /// <summary>每 3 天，對每個 12 格內的鄰居各擲一次。</summary>
        public static float SpreadChance(float severity, int neighbors, CageVenue venue, float targetResistance = 1f)
        {
            float chance = SpreadBase * Mathf.Clamp01(severity) * Crowding(neighbors) * (venue == CageVenue.Bay ? BayVenueFactor : 1f);
            return Mathf.Clamp01(chance / Mathf.Max(0.1f, targetResistance));
        }

        public static float AfterDay(float severity)
        {
            return severity <= 0f ? 0f : Mathf.Min(1f, severity + DailyRise);
        }

        /// <summary>孤立（12 格內沒有其他箱網）10 天後開始每日 −0.02。</summary>
        public static float AfterIsolatedDay(float severity, int isolatedDays)
        {
            if (severity <= 0f)
            {
                return 0f;
            }
            if (isolatedDays < SelfHealDays)
            {
                return AfterDay(severity);
            }
            return Mathf.Max(0f, severity - SelfHealPerDay);
        }

        public static float AfterTreatment(float severity, bool herbal)
        {
            return Mathf.Max(0f, severity - TreatReduction * (herbal ? HerbalFactor : 1f));
        }
    }
}
