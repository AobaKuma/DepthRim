using System;
using System.IO;
using DepthRim;
using UnityEngine;

// 海面擺動：左右為主、上下為輔；速度是位移的導數；天氣倍率；鏡頭擺動很小；著色器用同一組式子。
static partial class Test
{
    static void SeaSwayMath()
    {
        float a = SeaSway.BaseAmplitude;
        float maxX = 0f, maxZ = 0f;
        bool reverses = false;
        float lastSign = 0f;
        float maxVelErr = 0f;
        for (float t = 0f; t < 200f; t += 0.05f)
        {
            Vector2 d = SeaSway.Offset(t, a);
            maxX = Math.Max(maxX, Math.Abs(d.x));
            maxZ = Math.Max(maxZ, Math.Abs(d.y));
            // 速度 = 位移的導數（數值微分比對）
            Vector2 numeric = (SeaSway.Offset(t + 0.001f, a) - SeaSway.Offset(t - 0.001f, a)) / 0.002f;
            Vector2 v = SeaSway.Velocity(t, a);
            maxVelErr = Math.Max(maxVelErr, (numeric - v).magnitude);
            float sign = Math.Sign(v.x);
            if (lastSign != 0f && sign != 0f && sign != lastSign) { reverses = true; }
            if (sign != 0f) { lastSign = sign; }
        }
        Check(maxX > 0.9f * a && maxX < 1.4f * a, $"left-right sway reaches about the amplitude ({maxX:F2} of {a})");
        Check(maxZ > 0.1f * a && maxZ < 0.6f * maxX, $"up-down sway is smaller than left-right ({maxZ:F2} vs {maxX:F2})");
        Check(reverses, "the water swings back and forth (the flow reverses)");
        Check(maxVelErr < 0.01f, $"velocity is the derivative of the offset (max error {maxVelErr:F4})");
        Near(SeaSway.Offset(3f, 0f).magnitude, 0f, "zero amplitude, no sway");

        // 天氣倍率：平靜 1，風越大越大，有上限
        Near(SeaSway.WeatherFactor(0f), 1f, "no wind: calm sea");
        Near(SeaSway.WeatherFactor(0.5f), 1f, "light wind: still calm");
        Check(SeaSway.WeatherFactor(1.5f) > SeaSway.WeatherFactor(1f), "stronger wind sways more");
        Check(SeaSway.WeatherFactor(1.8f) >= 2f, "a storm (wind about 1.8) at least doubles the sway");
        Near(SeaSway.WeatherFactor(10f), SeaSway.MaxWeatherFactor, "capped in extreme wind");

        // 鏡頭：非常小，暴風時大一點
        float camCalm = 0f, camStorm = 0f;
        for (float t = 0f; t < 30f; t += 0.02f)
        {
            camCalm = Math.Max(camCalm, Math.Abs(SeaSway.CameraOffset(t, 1f)));
            camStorm = Math.Max(camStorm, Math.Abs(SeaSway.CameraOffset(t, SeaSway.MaxWeatherFactor)));
        }
        Check(camCalm > 0.01f && camCalm <= 0.05f, $"the camera bob is very small in calm weather ({camCalm:F3} cells)");
        Check(camStorm > camCalm * 2f && camStorm <= 0.1f, $"bigger in a storm but still small ({camStorm:F3} cells)");
        Check(Math.Abs(SeaSway.CameraOffset(1f, 1f) - SeaSway.CameraOffset(1.02f, 1f)) < 0.002f, "the bob is smooth frame to frame");
        Check(new DepthRimModSettings().seaCameraSway, "the camera bob is on by default (and can be turned off)");

        // 著色器用同一組式子
        string frag = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Shaders", "DepthRim_SeaSurface.frag.glsl"));
        foreach (string term in new[] { "sin(0.55 * t)", "0.3 * sin(1.37 * t + 1.3)", "sin(0.41 * t + 0.8)", "0.4 * sin(1.11 * t + 2.1)",
                                          "0.55 * cos(0.55 * t)", "0.3 * 1.37 * cos(1.37 * t + 1.3)", "0.41 * cos(0.41 * t + 0.8)", "0.4 * 1.11 * cos(1.11 * t + 2.1)" })
        {
            Check(frag.Contains(term), $"the shader's sway uses the same term as C# ({term})");
        }
        Check(frag.Contains("vec4 _Sway;") && frag.Contains("world - downstream * wakeLength"), "the pillar wake trails downstream of the current flow");
        Check(!frag.Contains("normalize(vec2(0.80, 0.60))"), "waves no longer run diagonally");
        // 浪花各處各自擺：相位來自平移中的雜訊圖，尾流與水線白沫用同一個局部流向
        Check(frag.Contains("float foamT = t + foamPhase(world, t);") && frag.Contains("vec2 flow = swayVelocity(foamT);"), "foam sways with a per-location phase, not the global sway");
        Check(frag.Contains("fbm(foamUv * 3.0") && frag.Contains("vec2 scroll = u._FoamPhase.zw * t;"), "the foam pattern follows the local sway and the phase field scrolls");
        UnityEngine.Vector4 fp = MapComponent_SeaSurface.FoamPhaseParams;
        Check(frag.Contains($"vec4(12.0, 5.7, 0.01, 0.006)") && fp == new UnityEngine.Vector4(12f, 5.7f, 0.01f, 0.006f), "the shader's foam-phase default matches C#");
        float period = 2f * UnityEngine.Mathf.PI / 0.55f;
        Check(fp.y >= period * 0.35f && fp.y <= period * 0.65f, $"foam phases spread about half a sway period ({fp.y:F1}s of {period:F1}s), so neighbours swing opposite ways");    }
}
