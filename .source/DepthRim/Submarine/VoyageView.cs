using System;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 航行中潛艇地圖（父物件是 <see cref="WorldObject_Submarine"/>）的畫面：
    /// <list type="bullet">
    /// <item>艙外幾乎看不到：離船體越遠越接近地圖界外的顏色（<see cref="SectionLayer_VoyageDarkness"/>）。</item>
    /// <item>鏡頭持續、不明顯的週期性晃動（<see cref="CameraOffset"/>，掛在 <see cref="Patch_SeaCameraSway"/>）。</item>
    /// </list>
    /// 數學部分是純函式，可單元測試。
    /// </summary>
    public static class VoyageView
    {
        /// <summary>地圖界外的顏色（原版鏡頭背景，實測 25/255 灰）。</summary>
        public static readonly Color OutOfBoundsColor = new Color(25f / 255f, 25f / 255f, 25f / 255f, 1f);

        /// <summary>離船體幾格內開始淡出（貼著船殼的那一圈仍看得到一點水）。</summary>
        public const float FadeStart = 0.5f;

        /// <summary>離船體幾格以外完全淡成界外色（<see cref="MaxAlpha"/>）。</summary>
        public const float FadeCells = 7f;

        /// <summary>最遠處的覆蓋程度：幾乎全蓋，只留一點點水色。</summary>
        public const float MaxAlpha = 0.97f;

        /// <summary>晃動的幅度（格）：主要左右、上下較小。</summary>
        public const float SwayAmplitude = 0.05f;

        /// <summary>晃動一次的週期（真實秒數）。</summary>
        public const float SwayPeriod = 9f;

        /// <summary>停在海中（沒有前進）時晃動的比例。</summary>
        public const float IdleSwayShare = 0.6f;

        /// <summary>這張地圖是不是航行中的潛艇（船體還在自己的地圖上，還沒下錨）。</summary>
        public static bool IsVoyageMap(Map map)
        {
            // 水面航行的船（M2）不用這個：海面照畫，另外畫航跡（SeaWake）
            return map?.Parent is WorldObject_Submarine;
        }

        /// <summary>
        /// 每格離最近的船體格有多遠（格，3-4 倒角距離，接近歐氏距離），上限 <paramref name="cap"/>。
        /// 船體格是 0。地圖上沒有任何船體時全部是 <paramref name="cap"/>。
        /// </summary>
        public static float[] DistanceField(int width, int height, Func<int, int, bool> isHull, float cap)
        {
            const int Straight = 3;
            const int Diagonal = 4;
            int big = (int)Math.Ceiling(cap * Straight) + Diagonal;
            int[] d = new int[width * height];
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    d[z * width + x] = isHull(x, z) ? 0 : big;
                }
            }
            // 正向：左、下、左下、右下
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = z * width + x;
                    int v = d[i];
                    if (x > 0) { v = Math.Min(v, d[i - 1] + Straight); }
                    if (z > 0)
                    {
                        v = Math.Min(v, d[i - width] + Straight);
                        if (x > 0) { v = Math.Min(v, d[i - width - 1] + Diagonal); }
                        if (x < width - 1) { v = Math.Min(v, d[i - width + 1] + Diagonal); }
                    }
                    d[i] = v;
                }
            }
            // 反向：右、上、右上、左上
            for (int z = height - 1; z >= 0; z--)
            {
                for (int x = width - 1; x >= 0; x--)
                {
                    int i = z * width + x;
                    int v = d[i];
                    if (x < width - 1) { v = Math.Min(v, d[i + 1] + Straight); }
                    if (z < height - 1)
                    {
                        v = Math.Min(v, d[i + width] + Straight);
                        if (x < width - 1) { v = Math.Min(v, d[i + width + 1] + Diagonal); }
                        if (x > 0) { v = Math.Min(v, d[i + width - 1] + Diagonal); }
                    }
                    d[i] = v;
                }
            }
            float[] result = new float[d.Length];
            for (int i = 0; i < d.Length; i++)
            {
                result[i] = Math.Min(cap, d[i] / (float)Straight);
            }
            return result;
        }

        /// <summary>離船體 <paramref name="distance"/> 格處的覆蓋程度：貼著船殼 0，平滑升到 <see cref="MaxAlpha"/>。</summary>
        public static float FadeAlpha(float distance)
        {
            float x = Mathf.Clamp01((distance - FadeStart) / (FadeCells - FadeStart));
            return MaxAlpha * x * x * (3f - 2f * x);
        }

        /// <summary>
        /// 鏡頭在真實時間 <paramref name="realTime"/> 的位移（格，x 東西、y 南北）。
        /// 幾個不成整數比的正弦相加，看起來是持續的緩慢搖晃、不像節拍器。
        /// <paramref name="diveMix"/>（0..1）= 正在上浮或下潛的程度：巡航時以左右搖擺為主，換深度時改成較慢的前後俯仰。
        /// </summary>
        public static Vector2 CameraOffset(float realTime, float strength, float diveMix = 0f)
        {
            diveMix = Mathf.Clamp01(diveMix);
            float phase = realTime * Mathf.PI * 2f / SwayPeriod;
            float roll = Mathf.Sin(phase) + 0.3f * Mathf.Sin(phase * 2.17f + 1.1f);
            float bob = 0.45f * Mathf.Sin(phase * 0.73f + 0.6f) + 0.15f * Mathf.Sin(phase * 1.91f + 2.4f);
            // 換深度：前後俯仰（週期約 1.6 倍長），左右收小
            float pitch = Mathf.Sin(phase * 0.62f + 0.3f) + 0.25f * Mathf.Sin(phase * 1.37f + 1.9f);
            float x = roll * (1f - 0.6f * diveMix);
            float z = Mathf.Lerp(bob, pitch, diveMix);
            return new Vector2(x, z) * (SwayAmplitude * strength / 1.3f);
        }

        // ── 艙外的漂流微粒：巡航時從船旁流過，下潛時往外擴散、上浮時往內收 ──

        /// <summary>全速巡航時微粒流過的速度（格／秒）。</summary>
        public const float CruiseFlowSpeed = 1.6f;

        /// <summary>沒有前進時微粒的緩慢漂移（格／秒）。</summary>
        public static readonly Vector2 IdleDrift = new Vector2(0.06f, 0.025f);

        /// <summary>換深度時微粒放射的速度（每秒放大幾倍的以 2 為底的對數：0.18 ≈ 每 5.5 秒放大一倍）。</summary>
        public const float DiveZoomRate = 0.18f;

        /// <summary>速度變化的平滑時間（秒）：開船、停船、開始下潛都是慢慢加速，不會一下子跳。</summary>
        public const float MotionSmoothing = 1.5f;

        /// <summary>
        /// 微粒的目標速度（格／秒）：前進中往推進方向的反方向流過（船往前、海水往後），速度乘上推力比例；
        /// 沒有前進或不知道方向時只緩慢漂移。
        /// </summary>
        public static Vector2 TargetFlow(bool moving, float thrustFraction, Vector2 heading)
        {
            if (!moving || heading.sqrMagnitude < 0.0001f)
            {
                return IdleDrift;
            }
            return -heading.normalized * (CruiseFlowSpeed * Mathf.Clamp01(thrustFraction)) + IdleDrift * (1f - Mathf.Clamp01(thrustFraction));
        }

        /// <summary>換深度的方向：+1 往下潛、-1 往上浮、0 沒在換。</summary>
        public static int DiveDirection(DepthTier current, DepthTier target, bool diving)
        {
            if (!diving || current == target)
            {
                return 0;
            }
            return target > current ? 1 : -1;
        }

        /// <summary>放射速度：下潛為正（微粒往外擴散，像是往海底靠近），上浮為負（往內收）。</summary>
        public static float TargetZoomRate(int diveDirection)
        {
            return diveDirection * DiveZoomRate;
        }

        /// <summary>往目標值指數趨近（時間常數 <see cref="MotionSmoothing"/>）。</summary>
        public static float Approach(float current, float target, float dt)
        {
            if (dt <= 0f)
            {
                return current;
            }
            return target + (current - target) * Mathf.Exp(-dt / MotionSmoothing);
        }

        public static Vector2 Approach(Vector2 current, Vector2 target, float dt)
        {
            return new Vector2(Approach(current.x, target.x, dt), Approach(current.y, target.y, dt));
        }

        /// <summary>晃動強度：前進中 1，停在海中 <see cref="IdleSwayShare"/>。</summary>
        public static float SwayStrength(bool moving)
        {
            return moving ? 1f : IdleSwayShare;
        }
    }
}
