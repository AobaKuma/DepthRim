using DMSE;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 航行地圖上「船在動」的畫面狀態：每幀依潛艇是否前進、往哪個方向、是否正在上浮或下潛，平滑地更新微粒的速度，
    /// 並把累積的位移交給艙外黑暗的著色器（<c>DepthRim_VoyageDarkness</c> 的 <c>_Flow</c>、<c>_Motion</c>）。
    ///
    /// <para>時間用遊戲時間（ticks / 60）：暫停時微粒停住，加速時流得快。位移在這裡累積而不是在著色器裡用「時間 × 速度」，
    /// 所以速度改變（開船、停船、開始下潛）時畫面不會跳。只追蹤目前正在看的地圖。</para>
    /// </summary>
    public static class VoyageMotion
    {
        private static Map map;
        private static int lastTick = -1;
        private static Vector2 flow;
        private static float zoomRate;
        private static Vector2 offset;
        private static float zoom;
        private static Vector2 heading;
        private static int nextHeadingFrame;

        /// <summary>目前微粒的速度（格／秒）。</summary>
        public static Vector2 Flow => flow;

        /// <summary>目前的放射速度（下潛為正）。</summary>
        public static float ZoomRate => zoomRate;

        /// <summary>正在換深度的程度（0..1，平滑過）：鏡頭搖晃與微粒的混合都看它。</summary>
        public static float DiveMix => Mathf.Clamp01(Mathf.Abs(zoomRate) / VoyageView.DiveZoomRate);

        /// <summary>累積的流動位移（格）。</summary>
        public static Vector2 Offset => offset;

        /// <summary>累積的放射量（以 2 為底的對數）。</summary>
        public static float Zoom => zoom;

        /// <summary>給驗收測試：最近一次算出的航向。</summary>
        public static Vector2 Heading => heading;

        /// <summary>這張地圖的潛艇正在換深度的程度（不是目前追蹤的地圖時 0）。</summary>
        public static float DiveMixOn(Map m)
        {
            return m == map ? DiveMix : 0f;
        }

        /// <summary>每幀呼叫（目前地圖、地圖畫面中）。</summary>
        public static void Update(Map current, Material material)
        {
            if (!(current?.Parent is WorldObject_Submarine sub))
            {
                return;
            }
            int tick = Find.TickManager.TicksGame;
            if (map != current)
            {
                map = current;
                lastTick = tick;
                flow = VoyageView.IdleDrift;
                zoomRate = 0f;
                nextHeadingFrame = 0;
            }
            if (Time.frameCount >= nextHeadingFrame)
            {
                // 航向 = 運作中推進器的推力方向（和 DMSE 出港動畫同一個算法）；勘查有快取，但仍不用每幀問
                Vector3? dir = TransferEngineEffects.ThrustDirection(sub.FlightSurvey());
                heading = dir.HasValue ? new Vector2(dir.Value.x, dir.Value.z) : Vector2.zero;
                nextHeadingFrame = Time.frameCount + 120;
            }
            float dt = Mathf.Clamp((tick - lastTick) / 60f, 0f, 1f);
            lastTick = tick;
            bool moving = sub.InFlight && !sub.Stalled;
            Vector2 targetFlow = VoyageView.TargetFlow(moving, sub.ThrustFraction, heading);
            float targetZoom = VoyageView.TargetZoomRate(VoyageView.DiveDirection(sub.cruiseTier, sub.targetTier, sub.Diving));
            flow = VoyageView.Approach(flow, targetFlow, dt);
            zoomRate = VoyageView.Approach(zoomRate, targetZoom, dt);
            offset += flow * dt;
            zoom += zoomRate * dt;
            // 位移只用來取樣重複的雜訊格子，太大會失去浮點精度：繞回一個大週期
            if (Mathf.Abs(offset.x) > 4096f) { offset.x %= 4096f; }
            if (Mathf.Abs(offset.y) > 4096f) { offset.y %= 4096f; }
            if (Mathf.Abs(zoom) > 4096f) { zoom %= 1f; }
            if (material != null)
            {
                IntVec3 c = current.Center;
                material.SetVector("_Flow", new Vector4(offset.x, offset.y, zoom, DiveMix));
                material.SetVector("_Motion", new Vector4(flow.x, flow.y, c.x + 0.5f, c.z + 0.5f));
            }
        }
    }
}
