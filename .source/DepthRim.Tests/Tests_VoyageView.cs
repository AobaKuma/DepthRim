using System;
using System.IO;
using DepthRim;
using UnityEngine;

// 航行中潛艇地圖：艙外淡出到界外色（距離場與覆蓋程度）、鏡頭持續不明顯地搖晃。
static partial class Test
{
    static void VoyageViewMath()
    {
        // 距離場：一格船體在 (5,5)
        float[] f = VoyageView.DistanceField(11, 11, (x, z) => x == 5 && z == 5, VoyageView.FadeCells);
        Near(f[5 * 11 + 5], 0f, "the hull cell itself is distance 0");
        Near(f[5 * 11 + 6], 1f, "the next cell is 1 away");
        Check(f[6 * 11 + 6] > 1.2f && f[6 * 11 + 6] < 1.5f, $"diagonal is about sqrt 2 ({f[6 * 11 + 6]:F2})");
        Near(f[5 * 11 + 9], 4f, "four cells away is 4");
        float[] wide = VoyageView.DistanceField(30, 1, (x, z) => x == 0, VoyageView.FadeCells);
        Near(wide[29], VoyageView.FadeCells, "capped at the fade distance");
        float[] none = VoyageView.DistanceField(4, 4, (x, z) => false, VoyageView.FadeCells);
        Near(none[7], VoyageView.FadeCells, "no hull: everything is far");

        // 覆蓋程度：貼著船殼看得到，越遠越不透明，最遠幾乎全蓋
        Near(VoyageView.FadeAlpha(0f), 0f, "no cover on the hull edge");
        Near(VoyageView.FadeAlpha(VoyageView.FadeStart), 0f, "no cover until the fade starts");
        Check(VoyageView.FadeAlpha(2f) > 0f && VoyageView.FadeAlpha(2f) < VoyageView.FadeAlpha(4f), "cover grows with distance");
        Near(VoyageView.FadeAlpha(VoyageView.FadeCells), VoyageView.MaxAlpha, "full cover at the fade distance");
        Near(VoyageView.FadeAlpha(100f), VoyageView.MaxAlpha, "stays at max far away");
        Check(VoyageView.MaxAlpha >= 0.9f && VoyageView.MaxAlpha < 1f, "outside the hull is almost (not completely) invisible");
        Near(VoyageView.OutOfBoundsColor.r, 25f / 255f, "fades to the out-of-bounds grey");

        // 鏡頭：持續、很小、平滑；停在海中時小一點
        float maxX = 0f, maxZ = 0f, maxStep = 0f;
        Vector2 prev = VoyageView.CameraOffset(0f, 1f);
        for (float t = 0.02f; t < 60f; t += 0.02f)
        {
            Vector2 o = VoyageView.CameraOffset(t, 1f);
            maxX = Math.Max(maxX, Math.Abs(o.x));
            maxZ = Math.Max(maxZ, Math.Abs(o.y));
            maxStep = Math.Max(maxStep, (o - prev).magnitude);
            prev = o;
        }
        Check(maxX > 0.02f && maxX <= 0.08f, $"the voyage sway is small ({maxX:F3} cells)");
        Check(maxZ > 0.005f && maxZ < maxX, $"up-down is smaller than side-to-side ({maxZ:F3})");
        Check(maxStep < 0.002f, $"smooth frame to frame ({maxStep:F4})");
        Check(VoyageView.SwayStrength(false) < VoyageView.SwayStrength(true) && VoyageView.SwayStrength(false) > 0f, "still sways when stopped, but less");

        // 著色器與 Def
        string frag = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Shaders", "DepthRim_VoyageDarkness.frag.glsl"));
        Check(frag.Contains("vec3 col = u._Color.rgb;") && frag.Contains("outColor = vec4(col, a);"), "the darkness shader paints the out-of-bounds colour");
        Check(frag.Contains("vec4 _Flow;") && frag.Contains("vec4 _Motion;") && frag.Contains("radialSpecks") && frag.Contains("cruiseSpecks"), "the darkness carries cruise and dive specks");

        // 巡航：微粒往航向的反方向流；停船時只緩慢漂移；推力一半時速度一半左右
        Vector2 east = new Vector2(1f, 0f);
        Vector2 full = VoyageView.TargetFlow(true, 1f, east);
        Check(full.x < -VoyageView.CruiseFlowSpeed * 0.9f && Math.Abs(full.y) < 0.01f, $"cruising east, the water streams west ({full})");
        Check(VoyageView.TargetFlow(true, 0.5f, east).magnitude < full.magnitude * 0.7f, "half thrust, slower stream");
        Check(VoyageView.TargetFlow(false, 1f, east) == VoyageView.IdleDrift, "stopped: only a slow drift");
        Check(VoyageView.TargetFlow(true, 1f, Vector2.zero) == VoyageView.IdleDrift, "no known heading: slow drift");
        Check(VoyageView.IdleDrift.magnitude < 0.2f, "the idle drift is slow");

        // 上浮下潛：方向與放射速度
        Check(VoyageView.DiveDirection(DepthTier.Shallow, DepthTier.Seabed, true) == 1, "heading deeper is diving (+1)");
        Check(VoyageView.DiveDirection(DepthTier.Seabed, DepthTier.Shallow, true) == -1, "heading shallower is surfacing (-1)");
        Check(VoyageView.DiveDirection(DepthTier.Seabed, DepthTier.Shallow, false) == 0 && VoyageView.DiveDirection(DepthTier.Seabed, DepthTier.Seabed, true) == 0, "not changing depth: 0");
        Check(VoyageView.TargetZoomRate(1) > 0f && VoyageView.TargetZoomRate(-1) < 0f && VoyageView.TargetZoomRate(0) == 0f, "diving spreads out, surfacing draws in");

        // 平滑：一個時間常數後走了約 63%，不會超過
        Near(VoyageView.Approach(0f, 1f, VoyageView.MotionSmoothing), 1f - (float)Math.Exp(-1), "smooth approach after one time constant");
        Near(VoyageView.Approach(0.3f, 1f, 0f), 0.3f, "no time, no change");
        Check(VoyageView.Approach(0f, 1f, 100f) <= 1f, "never overshoots");

        // 鏡頭：巡航以左右為主；換深度時前後為主、左右收小
        float cx = 0f, cz = 0f, dx = 0f, dz = 0f;
        for (float t = 0f; t < 90f; t += 0.05f)
        {
            Vector2 c = VoyageView.CameraOffset(t, 1f, 0f);
            Vector2 d = VoyageView.CameraOffset(t, 1f, 1f);
            cx = Math.Max(cx, Math.Abs(c.x)); cz = Math.Max(cz, Math.Abs(c.y));
            dx = Math.Max(dx, Math.Abs(d.x)); dz = Math.Max(dz, Math.Abs(d.y));
        }
        Check(cx > cz, $"cruising rocks side to side ({cx:F3} vs {cz:F3})");
        Check(dz > dx && dz > cz, $"changing depth pitches fore and aft ({dz:F3} vs {dx:F3})");
        Check(dz <= 0.08f, $"the pitch is still small ({dz:F3} cells)");
        Check((VoyageView.CameraOffset(10f, 1f) - VoyageView.CameraOffset(10f, 1f, 0f)).magnitude < 1e-6f, "default is the cruise sway");
        string defs = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Defs", "Rendering", "WaterVolume_Shader.xml"));
        Check(defs.Contains("<defName>DepthRim_VoyageDarkness</defName>") && defs.Contains("<renderQueue>3182</renderQueue>"), "the darkness draws after the vanilla lighting (3180)");
    }
}
