#version 450

// DepthRim 平台底下的暗化層（DepthRim.SectionLayer_PlatformShadow）：高出海面的平台造成的暗，蓋在平台底下的海面、浮船、建築與 Pawn 上。
// 頂點著色器沿用原版 MapWorldUV.vert.glsl（vUv = 世界座標 XZ）。
//
// 兩種暗（2026-11-15，DepthRim.PlatformShadow）：
//   - 天光遮蔽：甲板正下方，大半徑模糊（遮罩 a），日夜都在、很淡；
//   - 陽光投影：甲板（遮罩 r）與支柱（遮罩 g 的入水處往北延伸成整個斷面，沿投影方向取樣）擋住太陽，方向、長度、強度照原版建築影子（_Cast，每幀由 C# 設）；
//     影子越長邊緣越柔（混進大半徑模糊）。
// 水面層再加一圈頭上甲板的輪廓（甲板在畫面上往北偏甲板高度），看得出上面有一層平台。
// 平台層往下看時（_Height.x = 海面落差）整體淡一點、柔一點（_Height.z、_Deck.y）。
// 頂點色 a = 1 − 燈光照亮影子的程度（C# 端依水面層的燈光算好）。
// C# 的 DepthRim.PlatformShadowField.Shade 是同一個算法（遊戲邏輯的光照），改這裡要一起改。

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _MapSize;   // @default = vec4(250.0, 250.0, 0.0, 0.0)   地圖大小（格）
    vec4 _Height;    // @default = vec4(14.1, 14.1, 1.0, 0.0)     x = 看下去海面往南偏幾格，y = 甲板離海面幾格，z = 影子濃度倍率（遠近）
    vec4 _Cast;      // @default = vec4(0.0, -1.75, 1.0, 0.0)     xy = 甲板影子的位移（格），z = 影子強度（時刻、天氣、日夜）
    vec4 _Deck;      // @default = vec4(0.12, 0.0, 0.55, 0.3)     x = 甲板輪廓濃度，y = 額外柔化，z = 陽光投影最暗，w = 天光遮蔽最暗
} u;

layout(set = 2, binding = 1) uniform sampler2D _MaskTex; // @default = black

// ---- platformShade begin（DepthRim_Pillar.frag.glsl 有一模一樣的一段；單元測試會比對）----
const int PILLAR_TAPS = 8;
// 支柱是圓柱：遮罩 g 只有入水處（前緣兩排），影子要用整個斷面算，所以沿柱子往北（往後）再取樣到 PILLAR_DEPTH 格
const int PILLAR_DEPTH_TAPS = 5;
const float PILLAR_DEPTH = 7.0;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

// 遮罩（世界座標 XZ，格；r = 甲板、g = 支柱入水處、b = 浮船、a = 甲板大半徑模糊）；地圖外一律 0
vec4 maskAt(vec2 world)
{
    vec2 uv = world / max(u._MapSize.xy, vec2(1.0));
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0)
        return vec4(0.0);
    return texture(_MaskTex, flipV(uv));
}

// 海面座標點 sea（高出海面 heightAbove 格）被遮住的程度：x = 陽光，y = 天光
vec2 platformShade(vec2 sea, float heightAbove)
{
    float deckHeight = max(u._Height.y, 0.001);
    if (heightAbove >= deckHeight)
        return vec2(0.0);
    float reach = 1.0 - heightAbove / deckHeight;
    vec2 offset = u._Cast.xy * reach;
    float soft = clamp(length(offset) / 16.0 * 0.7 + u._Deck.y, 0.0, 1.0);
    vec4 m = maskAt(sea - offset);
    float sun = smoothstep(0.05, 0.85, mix(m.r, m.a, soft));
    if (heightAbove <= 0.0)
    {
        float column = 0.0;
        for (int i = 1; i <= PILLAR_TAPS; i++)
        {
            vec2 along = sea - u._Cast.xy * (float(i) / float(PILLAR_TAPS));
            for (int j = 0; j < PILLAR_DEPTH_TAPS; j++)
                column = max(column, maskAt(along - vec2(0.0, PILLAR_DEPTH * float(j) / float(PILLAR_DEPTH_TAPS - 1))).g);
        }
        // 柱影從 g ≥ 0.35 才開始（PlatformShadow.PillarShadowStart）：箱網浮框的水線白沫在 g 但壓在這以下，不投柱影
        sun = max(sun, smoothstep(0.35, 0.6, column));
    }
    return vec2(sun, smoothstep(0.1, 0.9, maskAt(sea).a));
}

// 兩種暗疊起來的濃度
float platformDarkness(vec2 shade)
{
    return 1.0 - (1.0 - u._Deck.z * u._Cast.z * shade.x) * (1.0 - u._Deck.w * shade.y);
}
// ---- platformShade end ----

void main()
{
    // 這個像素看到的海面座標（和海面著色器一樣）
    vec2 sea = vUv + vec2(0.0, u._Height.x);
    float dark = platformDarkness(platformShade(sea, 0.0));
    // 水面層：頭上甲板的輪廓。甲板在畫面上往北偏甲板高度，邊緣一圈淡淡的暗線
    if (u._Deck.x > 0.0)
    {
        float d = maskAt(vUv - vec2(0.0, u._Height.y)).r;
        float edge = smoothstep(0.3, 0.5, d) * (1.0 - smoothstep(0.5, 0.7, d));
        dark = 1.0 - (1.0 - dark) * (1.0 - u._Deck.x * edge);
    }
    outColor = vec4(0.0, 0.0, 0.0, dark * vColor.a * u._Height.z);
}
