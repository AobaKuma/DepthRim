#version 450

// DepthRim 中層水域：從深淵底下經過的生物（DepthRim.MapDrawLayer_Midwater）。
// 畫在外海層（3177）之後、光照暗化（3180）之前，關閉深度測試：外海層在沒光的地方覆蓋 85% 以上，
// 畫在它下面就什麼都看不到（驗收截圖實測）。所以生物是水面上的一片「透過水看到的影子」，
// 夜色仍會由原版暗化層蓋上；亮的部分（眼睛、水母的發光管）另外加上 _GlowColor。
// 用深淵遮罩（每格一個像素，已探索的深淵 = 1）把生物裁在深淵裡：牠們從海床與岩體「底下」經過，
// 迷霧下也不會洩漏任何東西。
//   vUv     = 貼圖座標，u 沿著身體（0 = 尾、1 = 頭）
//   vWorld  = 世界座標 XZ（查遮罩用）
//   vColor  = r：擺動相位 0..1；a：淡入淡出

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vWorld;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)  色調；a = 整體不透明度
    vec4 _GlowColor;      // @color @default = vec4(0.0, 0.0, 0.0, 0.0)  發光部位（貼圖亮度 > 0.85）的顏色；a = 強度
    vec4 _MapSize;        // @default = vec4(250.0, 250.0, 0.0, 0.0)    地圖大小（格），遮罩座標 = vWorld / _MapSize.xy
    float _GameSeconds;   // 動畫時鐘；全域
    float _SwimAmplitude; // @default = 0.03   身體擺動幅度（佔貼圖寬的比例）
    float _SwimFrequency; // @default = 6.0    身體上同時有幾段波
    float _SwimSpeed;     // @default = 2.0    擺動速度
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 maskUv = vWorld / max(u._MapSize.xy, vec2(1.0));
    float mask = texture(_MaskTex, flipV(maskUv)).r;
    // 地圖外沒有遮罩資料，一律裁掉
    if (mask <= 0.002 || maskUv.x < 0.0 || maskUv.y < 0.0 || maskUv.x > 1.0 || maskUv.y > 1.0)
    {
        discard;
    }

    // 游動：橫向波從頭傳到尾，越靠尾巴擺得越大（頭部幾乎不動）
    float t = u._GameSeconds * u._SwimSpeed + vColor.r * 6.2831;
    float tail = 1.0 - vUv.x;
    vec2 uv = vUv;
    uv.y += sin(vUv.x * u._SwimFrequency - t) * u._SwimAmplitude * tail * tail;

    vec4 tex = texture(_MainTex, flipV(uv));
    float alpha = tex.a * u._Color.a * vColor.a * mask;
    if (alpha <= 0.002)
    {
        discard;
    }
    // 貼圖裡特別亮的部位（眼睛、發光管）當成生物光，不受色調壓暗
    float lum = dot(tex.rgb, vec3(0.299, 0.587, 0.114));
    float glow = smoothstep(0.84, 0.92, lum) * u._GlowColor.a;
    vec3 col = mix(tex.rgb * u._Color.rgb, u._GlowColor.rgb, glow);
    outColor = vec4(col, max(alpha, glow * tex.a * vColor.a * mask));
}
