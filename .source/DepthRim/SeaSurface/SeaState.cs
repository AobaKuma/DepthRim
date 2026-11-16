using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 掛在天氣上的海況（惡劣海況 <c>DepthRim_RoughSea</c>、暴風雨 <c>DepthRim_RoughSeaStorm</c>）：
    /// 海面地圖的水面層在這個天氣下浪有多大、看起來多兇、會捲走多少東西、打壞多少建築。沒有這個擴充的天氣全部是 0（只看風速）。
    /// </summary>
    public class SeaStateExtension : DefModExtension
    {
        /// <summary>海面擺動的天氣倍率至少這麼大（<see cref="SeaSway.WeatherFactor"/> 的下限；也決定落海風險，§3.2）。</summary>
        public float swayFloor = 1f;

        /// <summary>海面看起來多兇（0..1）：白浪、浪花、海色變暗、漣漪加強（著色器 _Rough）。</summary>
        public float roughness;

        /// <summary>戶外、靠海邊的一件物品平均多久被浪捲走（遊戲小時）；0 = 不會。</summary>
        public float itemSweepMtbHours;

        /// <summary>靠海邊的一座建築平均多久被浪打一次（遊戲小時）；0 = 不會。</summary>
        public float buildingHitMtbHours;

        /// <summary>每次打中的傷害。</summary>
        public FloatRange buildingDamage = new FloatRange(0f, 0f);
    }

    /// <summary>一張海面地圖現在的海況（天氣轉換中時在前後兩種天氣之間內插）。</summary>
    public struct SeaStateValues
    {
        public float swayFloor;
        public float roughness;
        public float itemSweepMtbHours;
        public float buildingHitMtbHours;
        public FloatRange buildingDamage;

        public static readonly SeaStateValues Calm = new SeaStateValues { swayFloor = 1f };

        public static SeaStateValues From(SeaStateExtension ext)
        {
            if (ext == null)
            {
                return Calm;
            }
            return new SeaStateValues
            {
                swayFloor = ext.swayFloor,
                roughness = ext.roughness,
                itemSweepMtbHours = ext.itemSweepMtbHours,
                buildingHitMtbHours = ext.buildingHitMtbHours,
                buildingDamage = ext.buildingDamage,
            };
        }

        /// <summary>
        /// 在兩種海況之間內插（<paramref name="t"/> = 0 是前一種、1 是目前的）。平均間隔對「機率率」內插，
        /// 0（不會發生）當成機率率 0，轉換到一半時頻率也是一半。
        /// </summary>
        public static SeaStateValues Lerp(SeaStateValues a, SeaStateValues b, float t)
        {
            t = Mathf.Clamp01(t);
            return new SeaStateValues
            {
                swayFloor = Mathf.Lerp(a.swayFloor, b.swayFloor, t),
                roughness = Mathf.Lerp(a.roughness, b.roughness, t),
                itemSweepMtbHours = LerpMtb(a.itemSweepMtbHours, b.itemSweepMtbHours, t),
                buildingHitMtbHours = LerpMtb(a.buildingHitMtbHours, b.buildingHitMtbHours, t),
                buildingDamage = new FloatRange(Mathf.Lerp(a.buildingDamage.min, b.buildingDamage.min, t), Mathf.Lerp(a.buildingDamage.max, b.buildingDamage.max, t)),
            };
        }

        public static float LerpMtb(float a, float b, float t)
        {
            float ra = a > 0f ? 1f / a : 0f;
            float rb = b > 0f ? 1f / b : 0f;
            float r = Mathf.Lerp(ra, rb, t);
            return r > 0f ? 1f / r : 0f;
        }
    }

    /// <summary>海況的查詢：都以水面層的天氣為準（平台層的天氣跟著水面層，<see cref="MapComponent_SeaWeather"/>）。</summary>
    public static class SeaState
    {
        /// <summary>測試用：不是 null 時取代天氣算出的海況。</summary>
        public static SeaStateValues? debugOverride;

        public static SeaStateValues Of(Map map)
        {
            if (debugOverride.HasValue)
            {
                return debugOverride.Value;
            }
            Map sea = SeaLevels.SeaLevelOf(map) ?? map;
            WeatherManager wm = sea?.weatherManager;
            if (wm?.curWeather == null || !SeaLevels.IsAnyLevel(map))
            {
                return SeaStateValues.Calm;
            }
            SeaStateValues cur = SeaStateValues.From(wm.curWeather.GetModExtension<SeaStateExtension>());
            if (wm.lastWeather == null || wm.lastWeather == wm.curWeather)
            {
                return cur;
            }
            SeaStateValues last = SeaStateValues.From(wm.lastWeather.GetModExtension<SeaStateExtension>());
            return SeaStateValues.Lerp(last, cur, wm.TransitionLerpFactor);
        }
    }
}
