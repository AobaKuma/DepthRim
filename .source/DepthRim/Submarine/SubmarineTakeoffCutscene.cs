using System;
using System.Collections.Generic;
using DMSE;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 潛艇的離港過場（2026-11-11：原本整段沿用轉移船的升空動畫）。DMSE 的 <see cref="TransferTakeoffCutscene"/> 負責擷取船體影像與流程，
    /// 這裡只換演出：沒有鏡頭震動與火箭聲（改成推進器的低頻運轉聲）、鏡頭不拉遠、船體沿推進方向緩緩滑出，
    /// 稍微變小（往深處潛）並沉進海水的顏色裡淡出；推進器只吐水泡（推進器沒有火焰，見 Submarine_Buildings.xml）。
    /// 由海洋圖層的 <c>TransferEnvironmentExtension.presentation.takeoffCutsceneClass</c> 指定。
    /// </summary>
    public class SubmarineTakeoffCutscene : TransferTakeoffCutscene
    {
        private Sustainer propeller;

        public SubmarineTakeoffCutscene(Map displayMap, Vector3 center, Action onComplete, Map captureMap = null, HashSet<IntVec3> cells = null)
            : base(displayMap, center, onComplete, captureMap, cells)
        {
        }


        protected override float Duration => 8f;

        protected override void StartEffects()
        {
            SoundDef loop = Presentation.thrusterLoopSound;
            propeller = loop?.TrySpawnSustainer(SoundInfo.OnCamera(MaintenanceType.PerFrame));
        }

        protected override void UpdateEffects(float elapsed, float progress)
        {
            if (propeller != null && !propeller.Ended)
            {
                // 推進器聲隨船遠去變小
                propeller.info.volumeFactor = 1f - FadeAt(progress) * 0.8f;
                propeller.Maintain();
            }
        }

        protected override void StopEffects()
        {
            if (propeller != null && !propeller.Ended)
            {
                propeller.End();
            }
            propeller = null;
        }

        protected override float ZoomAt(float progress)
        {
            // 鏡頭固定（2026-11-14：原本拉遠 15%）
            return InitialZoom;
        }

        protected override Vector3 ShipOffset(float progress)
        {
            // 水裡的船慢慢加速，不像火箭那樣衝出畫面
            return ThrustDirection * (InitialZoom * 1.4f * progress * progress);
        }

        protected override float ShipScale(float progress)
        {
            // 往深處潛只縮一點（2026-11-14：原本縮到 0.8），主要靠沉進海水的顏色與淡出
            return Mathf.Lerp(1f, 0.92f, Mathf.SmoothStep(0f, 1f, progress));
        }

        protected override float FadeAt(float progress)
        {
            return Mathf.InverseLerp(0.45f, 1f, progress);
        }

        protected override Color ShipColor(float progress)
        {
            // 先沉進海水的顏色（變暗、偏藍），再淡出
            float fade = FadeAt(progress);
            Color water = WaterVisualTierDef.For(FloodingUtility.DepthTierOf(DisplayMap))?.deepColor ?? new Color(0.03f, 0.12f, 0.22f);
            Color c = Color.Lerp(Color.white, water, Mathf.Clamp01(fade * 1.2f));
            c.a = 1f - fade;
            return c;
        }

        protected override float FlameAt(float progress)
        {
            return 0f;
        }
    }
}
