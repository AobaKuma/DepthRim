#version 450

// DepthRim 艙室層（設計稿 §2.2 視覺）：畫在 DepthRim.SectionLayer_WaterVolume 的逐格網格上。
// 頂點著色器沿用原版 MapWorldUV.vert.glsl（vUv = 世界座標 XZ）。頂點色由 C# 依艙室水位、地形與光照算好：
//   a = 水位 0..1（同一間艙室內插值，牆的兩側互不影響）
//   r = 岸邊程度 0..1（頂點周圍被岩體或牆佔住的比例）：邊緣淡出、加深並用雜訊打散直線
//   g = 光照值 0..1（頂點周圍四格 GlowGrid.VisualGlowAt 的亮度平均）：有燈的地方清澈、沒燈的地方混濁
// 波紋沿用原版的全域 _RippleTex 與 _GameSeconds（WaterDepth.frag.glsl 也是這兩個），動畫完全在 GPU 上算。
//
// 層級化（設計稿 §3.4）：讀 WaterVisualTierDef 的水色、深色、混濁度與波紋尺度；
// 艙室是人造環境，**預設**不套用海洋雪、生物光、霧氣或晃動——只有 Def 把
// `compartmentEffects` 設成 true 的層級才有海洋雪（二期 P2-6，`_SnowDensity` > 0）。

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;              // @color @default = vec4(0.10, 0.36, 0.55, 1.0)  淺水／艙室積水的顏色
    vec4 _DeepColor;          // @color @default = vec4(0.02, 0.10, 0.22, 1.0)  深處的顏色
    vec4 _CausticColor;       // @color @default = vec4(0.55, 0.85, 1.0, 1.0)   水底光紋與亮處水色
    float _GameSeconds;       // 動畫時鐘；全域
    float _ClearAlpha;        // @default = 0.15   光照充足時的覆蓋程度
    float _MurkAlpha;         // @default = 0.85   完全沒光時的覆蓋程度
    float _LightMin;          // @default = 0.05   低於這個光照值視為全暗
    float _LightMax;          // @default = 0.60   高於這個光照值視為全亮
    float _LightTint;         // @default = 0.25   亮處水色偏淺青的程度
    float _RippleScale;       // @default = 0.06   波紋取樣尺度：1 / 尺度 = 重複週期（格）
    float _RippleSpeed;       // @default = 1.0    漂移速度倍率
    float _RippleContrast;    // @default = 0.6    0 = 平滑起伏，1 = 銳利光紋
    float _RippleStrength;    // @default = 1.0    0 = 跳過波紋與光紋
    float _CausticStrength;   // @default = 0.35   光紋強度
    float _ShoreDarken;       // @default = 0.45   岸邊加深
    float _SnowDensity;       // @default = 0.0    艙室層海洋雪（0 = 跳過；只有 Def 的 compartmentEffects = true 時才有值）
    float _SnowSpeed;         // @default = 1.0    海洋雪下沉速度倍率
    float _MinAlpha;          // @default = 0.6    有光時的最低覆蓋程度（乘上水位曲線 0.5–1.5 倍），讓明亮艙室裡的積水也看得出來
    float _FoamStrength;      // @default = 0.8    半淹時靠牆一圈水線泡沫的強度（0 = 關閉）
} u;

layout(set = 2, binding = 1) uniform sampler2D _RippleTex; // 全域（TexGame: Other/Ripples）

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float valueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 w = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), w.x),
               mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), w.x), w.y);
}

// A：兩層方向不同、緩慢漂移的波紋相乘；尺度由材質參數控制
float ripple(vec2 world, float t)
{
    vec2 driftA = vec2(t * 0.012, t * 0.007) * u._RippleSpeed;
    vec2 driftB = vec2(t * 0.009, -t * 0.011) * u._RippleSpeed;
    vec2 a = world * u._RippleScale + driftA;
    vec2 b = mat2(0.8, -0.6, 0.6, 0.8) * world * (u._RippleScale * 1.45) - driftB;
    return textureLod(_RippleTex, a, 0.0).r * textureLod(_RippleTex, b, 0.0).r;
}

// A：光紋帶寬跟著尺度放寬；_RippleContrast 越低，帶越寬越柔
float causticBand(float h)
{
    float contrast = clamp(u._RippleContrast, 0.0, 1.0);
    float widen = mix(2.5, 1.0, contrast);
    float rise = 0.12 * widen;
    float fall = 0.20 * widen;
    return smoothstep(0.42 - rise, 0.42, h) * (1.0 - smoothstep(0.42, 0.42 + fall, h));
}

// B：亮 → 清澈、暗 → 混濁。和 FloodingMath.WaterAlpha 是同一條公式（單元測試對照）
float clarityOf(float light)
{
    float span = max(u._LightMax - u._LightMin, 0.0001);
    float x = clamp((light - u._LightMin) / span, 0.0, 1.0);
    return x * x * (3.0 - 2.0 * x);
}

// 艙室層海洋雪（二期 P2-6）：單層稀疏亮點，比外海層慢也比外海層暗；
// 每顆有自己的擺幅、頻率、相位與大小，所以不會整片同步移動。
float compartmentSnow(vec2 world, float t)
{
    if (u._SnowDensity <= 0.0)
        return 0.0;
    vec2 p = world + vec2(0.0, -t * 0.05 * u._SnowSpeed);
    vec2 q = p * 1.6;
    vec2 id = floor(q);
    vec2 f = fract(q) - 0.5;
    float r = hash12(id + 3.7);
    if (r > u._SnowDensity * 0.5)
        return 0.0;
    float phase = r * 31.0 + hash12(id + 8.1) * 6.2831;
    float sway = sin(t * (0.10 + 0.14 * hash12(id + 2.3)) + phase) * 0.10;
    float d = length(f - vec2(sway, 0.0));
    float size = 0.07 + 0.05 * hash12(id + 5.9);
    return (1.0 - smoothstep(size * 0.25, size, d)) * (0.5 + 0.5 * hash12(id + 1.1));
}

void main()
{
    float level = clamp(vColor.a, 0.0, 1.0);    float shore = clamp(vColor.r, 0.0, 1.0);
    float light = clamp(vColor.g, 0.0, 1.0);
    if (level <= 0.002)
        discard;

    float t = u._GameSeconds;
    vec2 world = vUv;

    // 北牆正面的水帶（頂點 b：0.5 = 頂邊水線、1.0 = 牆腳；艙室水面的 b 是 0）
    if (vColor.b > 0.25)
    {
        float fv = clamp((vColor.b - 0.5) * 2.0, 0.0, 1.0);
        // 水線起伏：頂邊依世界座標與時間上下擺動，擺動以上的部分丟掉
        float edge = 0.14 * (0.5 + 0.5 * sin(world.x * 5.0 + t * 1.4)) * (0.6 + 0.4 * sin(world.x * 1.7 - t * 0.6));
        if (fv < edge)
            discard;
        float faceClarity = clarityOf(light);
        vec3 fc = mix(u._Color.rgb, u._DeepColor.rgb, 0.3 + 0.45 * fv);
        fc = mix(fc, u._CausticColor.rgb, u._LightTint * faceClarity);
        float line = 1.0 - smoothstep(edge, edge + 0.16, fv);
        fc = mix(fc, vec3(0.82, 0.93, 0.98), line * clamp(u._FoamStrength, 0.0, 1.0));
        float fa = max(mix(0.55, 0.85, fv), line * 0.9);
        outColor = vec4(clamp(fc, 0.0, 1.0), clamp(fa, 0.0, 1.0));
        return;
    }

    // 顏色：水越深越接近深色；亮處往淺青偏移一些
    float clarity = clarityOf(light);
    vec3 col = mix(u._Color.rgb, u._DeepColor.rgb, level);
    col = mix(col, u._CausticColor.rgb, u._LightTint * clarity);

    // 水底光紋：波紋高度的細帶，越淺越明顯（深水把光吃掉）
    float h = u._RippleStrength > 0.0 ? ripple(world, t) : 1.0;
    float caustic = causticBand(h) * u._RippleStrength;
    col += u._CausticColor.rgb * caustic * u._CausticStrength * (1.0 - 0.6 * level);

    // 半淹的艙室：水面的細微反光
    float partial = smoothstep(0.02, 0.15, level) * (1.0 - smoothstep(0.85, 0.98, level));
    col += vec3(0.08, 0.12, 0.14) * partial * smoothstep(0.25, 0.5, h);

    // 艙室層海洋雪（二期 P2-6）：只在 Def 開啟 compartmentEffects 的層級有值，
    // 單層、比外海層慢也比外海層暗，避免小房間裡變成雜訊。
    float snow = compartmentSnow(world, t) * (1.0 - 0.4 * level);
    col += vec3(0.75, 0.82, 0.90) * snow * 0.6;

    // 岸邊：加深，並用雜訊把格子邊緣的直線打散
    float n = valueNoise(world * 2.3 + vec2(t * 0.05, 0.0)) * 0.6 + valueNoise(world * 5.1) * 0.4;
    // 岸邊只淡出一部分（最低 0.65）：完全淡出會在牆邊留下一條沒有水的縫（使用者回報）
    float edgeFade = 1.0 - 0.35 * smoothstep(0.35, 1.0, shore + (n - 0.5) * 0.5);
    col *= 1.0 - u._ShoreDarken * shore;

    // B：覆蓋程度只看光照，再乘上水位（水越少越透明）
    float alpha = mix(u._MurkAlpha, u._ClearAlpha, clarity);
    alpha *= smoothstep(0.0, 0.25, level) * mix(0.45, 1.0, level) * edgeFade;
    // 明亮的艙室：光照讓水幾乎透明，所以給一個隨水位上升的下限（預設：一成水 0.36、半淹 0.6、九成 0.84），積水一定看得到。
    // 下限只壓住岸邊淡出的一半，牆邊仍然柔和，但不會整圈消失
    float floorAlpha = u._MinAlpha * mix(0.5, 1.5, level) * smoothstep(0.0, 0.08, level) * mix(0.5, 1.0, edgeFade);
    // 被下限撐起來的部分改用較亮、較飽和的水色，和深色地板分得開（實測：原本的深藍在夜裡幾乎和地板同色）
    float lifted = clamp((floorAlpha - alpha) / max(floorAlpha, 0.001), 0.0, 1.0);
    col = mix(col, u._Color.rgb * 1.35 + vec3(0.02, 0.06, 0.08), lifted * 0.6);
    alpha = max(alpha, floorAlpha);

    // 水線：半淹時靠牆一圈淺色泡沫，緩慢起伏；滿水與乾的艙室沒有
    float foamWave = 0.65 + 0.35 * sin(t * 1.3 + world.x * 3.1 + world.y * 2.7 + n * 4.0);
    // 牆邊頂點的 shore 約 0.5（四格裡兩格是牆），往格子內側線性降到 0，所以門檻放低才看得到一條帶狀水線
    float foam = clamp(u._FoamStrength * partial * smoothstep(0.08, 0.4, shore) * foamWave, 0.0, 1.0);
    col = mix(col, vec3(0.80, 0.92, 0.97), foam * 0.75);
    alpha = max(alpha, foam * 0.85);

    outColor = vec4(clamp(col, 0.0, 1.0), clamp(alpha, 0.0, 1.0));
}
