using HarmonyLib;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海面的擺動：海水以左右為主、上下為輔來回擺盪（不是往一個方向一直流）。
    ///
    /// <para>位移 <see cref="Offset"/> 與速度 <see cref="Velocity"/> 是同一組正弦的和與導數；著色器（DepthRim_SeaSurface.frag.glsl 的
    /// <c>swayOffset</c>／<c>swayVelocity</c>）用一模一樣的式子，所以兩邊對得上。浪的花紋跟著位移擺；支柱與船身旁的白沫尾流
    /// 拖在水流的下游，水往回擺時尾流跟著換邊。</para>
    ///
    /// <para>幅度隨天氣：<see cref="WeatherFactor"/> 依地圖的風速，平靜時 1、暴風時最多 <see cref="MaxWeatherFactor"/>。
    /// 水面層的鏡頭也會跟著非常微小地上下擺（<see cref="CameraOffset"/>，可在模組設定關掉）。</para>
    /// </summary>
    public static class SeaSway
    {
        /// <summary>平靜時水平擺動的幅度（格）。</summary>
        public const float BaseAmplitude = 0.6f;

        /// <summary>上下擺動相對於左右的比例。</summary>
        public const float VerticalShare = 0.35f;

        public const float MinWeatherFactor = 1f;

        public const float MaxWeatherFactor = 2.5f;

        /// <summary>鏡頭上下擺動的幅度（格，乘上天氣倍率）：非常小。</summary>
        public const float CameraAmplitude = 0.035f;

        /// <summary>鏡頭擺一次的週期（真實秒數）。</summary>
        public const float CameraPeriod = 7f;

        /// <summary>
        /// 天氣倍率：風速 0.5 以下算平靜（1），之後每多 1 風速多 1.2 倍，最多 <see cref="MaxWeatherFactor"/>
        /// （原版暴風雨、雷雨的風速大約 1.5～2）。
        /// </summary>
        public static float WeatherFactor(float windSpeed)
        {
            return Mathf.Clamp(1f + 1.2f * (windSpeed - 0.5f), MinWeatherFactor, MaxWeatherFactor);
        }

        /// <summary>海水在時間 <paramref name="t"/>（秒）的位移（格）：x 左右、y 上下。</summary>
        public static Vector2 Offset(float t, float amplitude)
        {
            float x = Mathf.Sin(0.55f * t) + 0.3f * Mathf.Sin(1.37f * t + 1.3f);
            float z = VerticalShare * (Mathf.Sin(0.41f * t + 0.8f) + 0.4f * Mathf.Sin(1.11f * t + 2.1f));
            return new Vector2(x, z) * amplitude;
        }

        /// <summary><see cref="Offset"/> 對時間的導數（格／秒）：水正往哪邊流。</summary>
        public static Vector2 Velocity(float t, float amplitude)
        {
            float x = 0.55f * Mathf.Cos(0.55f * t) + 0.3f * 1.37f * Mathf.Cos(1.37f * t + 1.3f);
            float z = VerticalShare * (0.41f * Mathf.Cos(0.41f * t + 0.8f) + 0.4f * 1.11f * Mathf.Cos(1.11f * t + 2.1f));
            return new Vector2(x, z) * amplitude;
        }

        /// <summary>鏡頭在真實時間 <paramref name="realTime"/> 的上下位移（格，正 = 往北）。</summary>
        public static float CameraOffset(float realTime, float weatherFactor)
        {
            float phase = realTime * Mathf.PI * 2f / CameraPeriod;
            // 主週期加一點較快的小擺動，不會像節拍器一樣規律
            return CameraAmplitude * weatherFactor * (Mathf.Sin(phase) + 0.25f * Mathf.Sin(phase * 2.3f + 0.9f)) / 1.25f;
        }
    }

    /// <summary>水面層的鏡頭微微上下擺動；航行中的潛艇地圖則持續緩慢搖晃（<see cref="VoyageView.CameraOffset"/>）。模組設定可關。只動畫面，不改鏡頭的根位置，所以不影響捲動與縮放。</summary>
    [HarmonyPatch(typeof(CameraDriver), "ApplyPositionToGameObject")]
    public static class Patch_SeaCameraSway
    {
        /// <summary>給驗收測試：最近一次套上的位移（格）。</summary>
        public static float lastOffset;

        /// <summary>給驗收測試：最近一次在航行中潛艇地圖上套上的位移（格，x 東西、y 南北）。</summary>
        public static Vector2 lastVoyageOffset;

        [HarmonyPostfix]
        public static void Postfix()
        {
            lastOffset = 0f;
            lastVoyageOffset = Vector2.zero;
            if (!DepthRimModSettings.Current.seaCameraSway || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            Map map = Find.CurrentMap;
            if (map?.Parent is WorldObject_Submarine sub)
            {
                // 航行中的潛艇：持續、不明顯的搖晃，停在海中時小一點；巡航時左右搖擺、上浮下潛時前後俯仰
                Vector2 offset = VoyageView.CameraOffset(Time.realtimeSinceStartup, VoyageView.SwayStrength(sub.InFlight && !sub.Stalled), VoyageMotion.DiveMixOn(map));
                lastOffset = offset.y;
                lastVoyageOffset = offset;
                Camera subCamera = Find.Camera;
                if (subCamera != null)
                {
                    subCamera.transform.position += new Vector3(offset.x, 0f, offset.y);
                }
                return;
            }
            if (!SeaLevels.IsSeaLevel(map))
            {
                return;
            }
            MapComponent_SeaSurface comp = map.GetComponent<MapComponent_SeaSurface>();
            if (comp == null)
            {
                return;
            }
            lastOffset = SeaSway.CameraOffset(Time.realtimeSinceStartup, comp.SwayFactor);
            Camera camera = Find.Camera;
            if (camera != null)
            {
                camera.transform.position += new Vector3(0f, 0f, lastOffset);
            }
        }
    }
}
