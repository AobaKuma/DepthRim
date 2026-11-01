#version 450

// DepthRim 深淵背景層（二期 P2-10）：畫在 DepthRim_Abyss 格上、地形之下（BelowTerrain）。
// 頂點著色器沿用原版 MapWorldUV.vert.glsl（vUv = 世界座標 XZ）。頂點色由 C# 算好：
//   r = 離崖壁的權重 0..1（緊鄰崖壁 1，4 格以外 0）
//   g = 光照 0..1（與水面同一套 WaterLight，所以燈光只照亮崖邊）
// 視差：兩層不同速度的微粒，越「深」的層越小、越慢、越暗；拉遠鏡頭時微粒變淡避免雜訊。

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;              // @color @default = vec4(0.012, 0.020, 0.038, 1.0)  深淵底色
    vec4 _DeepColor;          // @color @default = vec4(0.002, 0.004, 0.010, 1.0)  最深的中心
    vec4 _RimColor;           // @color @default = vec4(0.32, 0.42, 0.52, 1.0)     崖邊被燈照亮的反光（只有靠崖那一圈）
    float _GameSeconds;       // 動畫時鐘；全域
    float _ParticleDensity;   // @default = 0.35   微粒密度（0 = 跳過）
    float _ParticleSpeed;     // @default = 0.5    微粒下沉速度倍率
    float _CameraZoom;        // @default = 14.0   鏡頭大小（拉遠時微粒變淡）
    float _SkyLight;          // @default = 0.0    目前日光強度（每幀由程式設定；頂點 a = 看得到天空的比例）
} u;

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// 一層微粒：每顆有自己的速度、相位、大小與亮度
float abyssParticleLayer(vec2 world, float t, float speed, float scale, float seed)
{
    if (u._ParticleDensity <= 0.0)
        return 0.0;
    vec2 p = world + vec2(seed * 11.3, -t * speed * u._ParticleSpeed);
    vec2 q = p * scale;
    vec2 id = floor(q);
    vec2 f = fract(q) - 0.5;
    float r = hash12(id + seed);
    if (r > u._ParticleDensity * 0.45)
        return 0.0;
    float phase = r * 37.0 + hash12(id + 4.7) * 6.2831;
    float sway = sin(t * (0.05 + 0.10 * hash12(id + 8.3)) + phase) * 0.12;
    float d = length(f - vec2(sway, 0.0));
    float size = 0.05 + 0.05 * hash12(id + 2.9);
    float bright = 0.30 + 0.45 * hash12(id + 6.1);
    return (1.0 - smoothstep(size * 0.25, size, d)) * bright;
}

float abyssParticles(vec2 world, float t)
{
    if (u._ParticleDensity <= 0.0)
        return 0.0;
    float a = abyssParticleLayer(world, t, 0.055, 1.10, 0.0);
    float b = abyssParticleLayer(world, t, 0.022, 2.30, 5.7);
    return max(a, b * 0.7);
}

void main()
{
    float rim = clamp(vColor.r, 0.0, 1.0);
    float light = max(clamp(vColor.g, 0.0, 1.0), clamp(vColor.a, 0.0, 1.0) * u._SkyLight);
    vec2 world = vUv;
    float t = u._GameSeconds;
    float far = smoothstep(18.0, 55.0, u._CameraZoom);

    // 底色：越靠中央（rim 越小）越黑
    vec3 col = mix(u._Color.rgb, u._DeepColor.rgb, 1.0 - rim);

    // 微粒：拉遠時變淡
    col += vec3(0.42, 0.60, 0.74) * abyssParticles(world, t) * (1.0 - 0.75 * far);

    // 深淵吸光：只有靠崖壁的地方吃得到光，中央永遠是全黑
    float reach = mix(0.45, 1.0, rim) * (0.25 + 0.75 * light * rim);
    col *= reach;

    // 崖邊被光反照：只有 rim 大又有光的地方才亮，形成一圈看得到崖壁的帶狀過渡。
    // 係數 1.2 + 較亮的 _RimColor：實測崖邊峰值只有 42/255（16% 灰），在一般螢幕上偏暗；
    // **不要**改用「把底色壓更暗」——核心已經是 5/255，再暗只會吃掉亮帶的對比空間。
    col += u._RimColor.rgb * rim * rim * light * 1.2;

    outColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
