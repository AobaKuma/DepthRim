#version 450

// DepthRim 外海層：畫在原版迷霧（3175）之上、光照暗化（3180）之前，關閉深度測試，
// 所以開放水域裡的岩體、建築、Pawn 都被蒙上一層水，未探索的迷霧區變成深海；燈光照明仍會作用在水上。
// 密閉艙室不在網格裡（DepthRim.SectionLayer_WaterOuter 不輸出那些格子），所以艙室沿格子邊界被乾淨地切掉。
// 頂點著色器沿用原版 MapWorldUV.vert.glsl（vUv = 世界座標 XZ）。頂點色：
//   r = 靠近艙室的程度 0..1：往艙室方向淡出，艙室的牆保持清楚
//   g = 光照值 0..1：頂點周圍四格 GlowGrid.VisualGlowAt 的亮度平均（迷霧格以 0 計入）
//   b = 迷霧比例 0..1：迷霧下更深、更不透明，蓋住原版迷霧的黑色
// 迷霧下的頂點不帶任何地形或光照資訊（C# 端保證），避免從水的樣子看出未探索的洞穴。
//
// 可見度（設計稿 §3.2）：光越亮水越清澈（_ClearAlpha），沒光的地方混濁（_MurkAlpha）；
// 迷霧區固定 _FogAlpha，不受光照影響。原版光照暗化（3180）仍然負責「暗」，這裡只負責「清不清楚」。
// 層級效果（設計稿 §3.3）：波紋尺度、光紋、光柱、海洋雪、生物光、沉積霧氣、變溫層晃動全部由材質參數控制，
// 參數為 0 時整段跳過；材質由 DepthRim.WaterTierMaterials 依 WaterVisualTierDef 建立。

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                // @color @default = vec4(0.06, 0.24, 0.40, 1.0)  已探索的開放水域
    vec4 _DeepColor;            // @color @default = vec4(0.02, 0.07, 0.15, 1.0)  深處水色（沉積霧氣往這裡拉）
    vec4 _FogColor;             // @color @default = vec4(0.015, 0.05, 0.11, 1.0) 迷霧下的深海
    vec4 _CausticColor;         // @color @default = vec4(0.50, 0.80, 1.0, 1.0)    光紋與亮處水色
    vec4 _BiolumColor;          // @color @default = vec4(0.35, 0.90, 0.85, 1.0)   生物光
    float _GameSeconds;         // 動畫時鐘；全域
    float _FogAlpha;            // @default = 0.93   迷霧區的覆蓋程度
    float _ClearAlpha;          // @default = 0.15   光照充足時的覆蓋程度
    float _MurkAlpha;           // @default = 0.85   完全沒光時的覆蓋程度
    float _LightMin;            // @default = 0.05   低於這個光照值視為全暗
    float _LightMax;            // @default = 0.60   高於這個光照值視為全亮
    float _LightTint;           // @default = 0.25   亮處水色偏淺青的程度
    float _RippleScale;         // @default = 0.03   波紋取樣尺度：1 / 尺度 = 重複週期（格）
    float _RippleSpeed;         // @default = 1.0    漂移速度倍率
    float _RippleContrast;      // @default = 0.6    0 = 平滑起伏，1 = 銳利光紋
    float _RippleStrength;      // @default = 0.8    0 = 跳過波紋與光紋
    float _CausticStrength;     // @default = 0.22   光紋強度
    float _LightShaftStrength;  // @default = 0.15   淺海光柱強度
    float _SnowDensity;         // @default = 0.0    海洋雪密度（0 = 跳過）
    float _SnowSpeed;           // @default = 1.0    海洋雪下沉速度倍率
    float _BiolumDensity;       // @default = 0.0    生物光密度（0 = 跳過）
    float _HazeStrength;        // @default = 0.0    海床沉積霧氣（0 = 跳過）
    float _WobbleStrength;      // @default = 0.0    變溫層假扭曲（0 = 跳過）
    float _CameraZoom;          // @default = 14.0   相機 rootSize；鏡頭拉遠時降低顆粒亮度
    float _SkyLight;            // @default = 0.0    目前日光強度（每幀由程式設定；頂點 a = 看得到天空的比例）
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

// A：兩層方向不同、緩慢漂移的波紋相乘；尺度與速度由材質參數控制（重複週期 = 1 / scale 格）
float ripple(vec2 world, float t)
{
    vec2 driftA = vec2(t * 0.010, t * 0.006) * u._RippleSpeed;
    vec2 driftB = vec2(t * 0.008, -t * 0.009) * u._RippleSpeed;
    vec2 a = world * u._RippleScale + driftA;
    vec2 b = mat2(0.8, -0.6, 0.6, 0.8) * world * (u._RippleScale * 1.45) - driftB;
    return textureLod(_RippleTex, a, 0.0).r * textureLod(_RippleTex, b, 0.0).r;
}

// 波紋強度為 0 時回傳 1.0（光紋帶自然為 0，不做兩次貼圖取樣）
float waterRipple(vec2 world, vec2 wobble, float t)
{
    if (u._RippleStrength <= 0.0)
        return 1.0;
    return ripple(world + wobble, t);
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

// C：淺海光柱：世界座標的低頻雜訊沿斜向拉成幾條亮帶，緩慢移動
float lightShaft(vec2 world, float t)
{
    if (u._LightShaftStrength <= 0.0)
        return 0.0;
    float slant = world.x * 0.045 + world.y * 0.075;
    float n = valueNoise(vec2(slant * 1.7 + t * 0.03, world.y * 0.04 - t * 0.02));
    return smoothstep(0.55, 0.82, n) * (1.0 - smoothstep(0.82, 0.98, n));
}

// D：海洋雪：一層稀疏亮點。下落速度、擺幅、頻率、相位、大小、亮度都由該顆自己的 hash 決定，
// 所以同一層裡的粒子不會同步移動；兩層不同速度（見 marineSnow）再拉出視差。
float marineSnowLayer(vec2 world, float t, float speed, float cellsPerScale, float density, float seed)
{
    if (density <= 0.0)
        return 0.0;
    vec2 p = world + vec2(seed * 7.31, -t * speed);
    float row = hash12(vec2(floor(p.y * 2.0), 3.1 + seed));
    vec2 q = vec2(p.x * cellsPerScale + row * 0.6 + seed * 3.7, p.y * cellsPerScale);
    vec2 id = floor(q);
    vec2 f = fract(q) - 0.5;
    float r = hash12(id + seed);
    if (r > density)
        return 0.0;
    // 每顆自己的擺動（振幅與頻率都不同）＋一點點自己的上下浮沉
    float phase = r * 41.0 + hash12(id + 7.3) * 6.2831;
    float amp = 0.05 + 0.16 * hash12(id + 3.3);
    float sway = sin(t * (0.08 + 0.20 * hash12(id + 9.1)) + phase) * amp;
    float bob = cos(t * (0.06 + 0.13 * hash12(id + 4.4)) + phase * 1.7) * 0.02;
    float d = length(f - vec2(sway, bob));
    float size = 0.07 + 0.06 * hash12(id + 7.3);
    float bright = 0.45 + 0.55 * hash12(id + 1.9);
    return (1.0 - smoothstep(size * 0.25, size, d)) * bright;
}

// D：兩層不同落下速度的海洋雪：慢的那層看起來更遠，合起來就不會「整片一起動」。
float marineSnow(vec2 world, float t)
{
    if (u._SnowDensity <= 0.0)
        return 0.0;
    float speed = 0.085 * u._SnowSpeed;
    float a = marineSnowLayer(world, t, speed,        1.7, u._SnowDensity * 0.55, 0.0);
    float b = marineSnowLayer(world, t, speed * 0.42, 2.5, u._SnowDensity * 0.40, 5.3);
    return max(a, b);
}

// D：生物光：非常稀疏的冷色光點，各自以不同相位緩慢明滅
float bioluminescence(vec2 world, float t)
{
    if (u._BiolumDensity <= 0.0)
        return 0.0;
    vec2 q = world * 0.55;
    vec2 id = floor(q);
    vec2 f = fract(q) - 0.5;
    float r = hash12(id + 11.7);
    if (r > u._BiolumDensity * 0.35)
        return 0.0;
    float phase = hash12(id + 5.3) * 6.2831;
    float pulse = 0.35 + 0.65 * pow(0.5 + 0.5 * sin(t * (0.20 + 0.35 * r) + phase), 2.0);
    vec2 center = (vec2(hash12(id + 2.7), hash12(id + 4.1)) - 0.5) * 0.7;
    float d = length(f - center);
    return (1.0 - smoothstep(0.0, 0.10, d)) * pulse;
}

// D：海床沉積霧氣：低頻雜訊，貼地的一層霧
float sedimentHaze(vec2 world, float t)
{
    if (u._HazeStrength <= 0.0)
        return 0.0;
    float n = valueNoise(world * 0.09 + vec2(t * 0.010, -t * 0.014));
    return smoothstep(0.35, 0.85, n);
}

// D：變溫層假扭曲：沿一個方向的正弦帶，偏移波紋取樣座標（不擷取畫面）
vec2 thermoclineWobble(vec2 world, float t)
{
    if (u._WobbleStrength <= 0.0)
        return vec2(0.0);
    float band = sin(world.x * 0.35 + world.y * 0.18 + t * 0.5);
    return vec2(0.0, band * 0.035 * u._WobbleStrength);
}

void main()
{
    float nearCompartment = clamp(vColor.r, 0.0, 1.0);
    // 人造光烘在頂點 g；日光 = 天空比例（a）× 每幀的 _SkyLight，不烘進網格，區塊不會因重建時間不同而明暗不一
    float light = max(clamp(vColor.g, 0.0, 1.0), clamp(vColor.a, 0.0, 1.0) * u._SkyLight);
    float fog = clamp(vColor.b, 0.0, 1.0);
    float t = u._GameSeconds;
    vec2 world = vUv;
    float far = smoothstep(18.0, 55.0, u._CameraZoom);

    // A：波紋與光紋
    vec2 wobble = thermoclineWobble(world, t);
    float h = waterRipple(world, wobble, t);
    float clarity = clarityOf(light);
    float caustic = causticBand(h) * u._RippleStrength;

    // 已探索的水色：光紋 + 亮處往淺青偏移（燈光看起來照進了水裡）
    vec3 openCol = u._Color.rgb + u._CausticColor.rgb * caustic * u._CausticStrength;
    openCol = mix(openCol, u._CausticColor.rgb, u._LightTint * clarity);

    // C：淺海光柱，暗處比較明顯（亮處被燈光蓋過）
    openCol += u._CausticColor.rgb * lightShaft(world, t) * u._LightShaftStrength * (1.0 - clarity);

    // D：海洋雪（被燈照亮的懸浮物；鏡頭拉遠時變淡）
    float snow = marineSnow(world, t) * (1.0 - 0.75 * far);
    openCol += vec3(0.75, 0.82, 0.90) * snow * mix(0.30, 1.0, clarity);

    // D：生物光（暗處明顯、亮處被燈光蓋過）
    float biolum = bioluminescence(world, t) * (1.0 - clarity);
    openCol += u._BiolumColor.rgb * biolum * u._BiolumDensity;

    // D：沉積霧氣（暗處濃、亮處淡）：把顏色往 deepColor 拉，並提高覆蓋程度
    float haze = sedimentHaze(world, t) * (1.0 - clarity);
    openCol = mix(openCol, u._DeepColor.rgb, haze * 0.55);

    // B：覆蓋程度只看光照；迷霧區另外用 _FogAlpha
    float alphaOpen = mix(u._MurkAlpha, u._ClearAlpha, clarity) + haze * 0.10;
    alphaOpen += (h - 0.35) * 0.03 * u._RippleStrength;

    // 迷霧：不讀光照，只留緩慢的大尺度起伏
    float swell = valueNoise(world * 0.08 + vec2(t * 0.02, -t * 0.015));
    vec3 fogCol = u._FogColor.rgb * mix(0.8, 1.25, swell);
    vec3 col = mix(openCol, fogCol, fog);
    float alpha = mix(alphaOpen, u._FogAlpha, fog);

    // 往艙室淡出，用雜訊打散格子直線；迷霧區不淡出（沒有艙室資訊）
    float n = valueNoise(world * 2.3 + vec2(t * 0.05, 0.0)) * 0.6 + valueNoise(world * 5.1) * 0.4;
    float fade = 1.0 - smoothstep(0.15, 0.85, nearCompartment + (n - 0.5) * 0.4);
    alpha *= mix(fade, 1.0, fog);

    outColor = vec4(clamp(col, 0.0, 1.0), clamp(alpha, 0.0, 1.0));
}
