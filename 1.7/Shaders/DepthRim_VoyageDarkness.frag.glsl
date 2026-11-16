#version 450

// DepthRim 航行黑暗（DepthRim.SectionLayer_VoyageDarkness）：航行中潛艇地圖的艙外蓋上地圖界外的顏色，
// 離船體越遠越不透明。排在原版光照暗化（3180）之後、關閉深度測試，所以界外色不受日夜與燈光影響。
// 頂點著色器沿用原版 MapWorldUV.vert.glsl（vUv = 世界座標 XZ）。頂點色 a = 覆蓋程度（C# 端依離船體的距離算好）。
// 用一點緩慢的低頻雜訊打散格子直線，邊緣看起來像光線被黑暗吃掉，而不是一圈方格。
//
// 黑暗裡有稀疏的懸浮微粒，讓人看得出船在怎麼動（DepthRim.VoyageMotion 每幀設定）：
//   巡航：微粒往推進方向的反方向流過，速度越快拖得越長（兩層不同速度，有前後景深）
//   下潛：微粒從船體往外擴散、越來越大（往海底靠近）；上浮：往內收、越來越小
// _Flow = (累積位移 x, 累積位移 z, 累積放射量（log2）, 換深度程度 0..1)
// _Motion = (目前速度 x, 目前速度 z, 船體中心 x, 船體中心 z)

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(0.098, 0.098, 0.098, 1.0)  地圖界外的顏色
    vec4 _SpeckColor;     // @color @default = vec4(0.55, 0.62, 0.70, 1.0)    懸浮微粒
    vec4 _Flow;           // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _Motion;         // @default = vec4(0.0, 0.0, 125.0, 125.0)
    float _SpeckStrength; // @default = 0.35   微粒的亮度（0 = 不畫）
    float _GameSeconds;   // 動畫時鐘；全域
} u;

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

// 一層稀疏微粒：q 是格子座標（已乘上密度），streak 是往後拖的長度（同一座標系）
float speckLayer(vec2 q, vec2 streak, float seed)
{
    vec2 id = floor(q);
    vec2 f = fract(q) - 0.5;
    float r = hash12(id + seed);
    if (r > 0.30)
        return 0.0;
    vec2 c = (vec2(hash12(id + seed + 1.7), hash12(id + seed + 4.3)) - 0.5) * 0.6;
    vec2 pa = f - c;
    float h = clamp(dot(pa, streak) / max(dot(streak, streak), 1e-5), 0.0, 1.0);
    float d = length(pa - streak * h);
    float size = 0.05 + 0.05 * hash12(id + seed + 7.1);
    float bright = 0.45 + 0.55 * hash12(id + seed + 2.9);
    return (1.0 - smoothstep(size * 0.3, size, d)) * bright * (1.0 - 0.6 * h);
}

// 巡航：兩層，近的快、遠的慢；拖尾 = 速度 × 0.35 秒（換成各層的格子座標）
float cruiseSpecks(vec2 world)
{
    vec2 vel = u._Motion.xy;
    float near = speckLayer((world - u._Flow.xy) * 0.40, -vel * 0.35 * 0.40, 0.0);
    float far = speckLayer((world - u._Flow.xy * 0.55) * 0.75, -vel * 0.55 * 0.35 * 0.75, 5.3) * 0.55;
    return max(near, far);
}

// 下潛／上浮：以船體中心為原點的無限縮放（兩層交替淡入淡出，接縫處兩層一模一樣，所以不會跳）
float radialSpecks(vec2 world)
{
    vec2 q = world - u._Motion.zw;
    float x = fract(u._Flow.z);
    float sum = 0.0;
    for (int i = 0; i < 2; i++)
    {
        float s = x + float(i);
        float scale = exp2(-s);
        float w = sin(3.14159265 * s * 0.5);
        sum = max(sum, speckLayer(q * scale * 0.45, vec2(0.0), 11.0) * w);
    }
    return sum;
}

void main()
{
    float a = clamp(vColor.a, 0.0, 1.0);
    float t = u._GameSeconds;
    float n = valueNoise(vUv * 0.9 + vec2(t * 0.03, -t * 0.02)) * 0.65 + valueNoise(vUv * 2.7) * 0.35;
    // 只在半透明的過渡帶起作用（a 接近 0 或接近最大值時不動）
    a = clamp(a + (n - 0.5) * 0.5 * a * (1.0 - a), 0.0, 1.0);

    vec3 col = u._Color.rgb;
    if (u._SpeckStrength > 0.0)
    {
        float dive = clamp(u._Flow.w, 0.0, 1.0);
        float speck = cruiseSpecks(vUv) * (1.0 - 0.6 * dive);
        if (dive > 0.001)
            speck = max(speck, radialSpecks(vUv) * dive);
        // 只畫在黑暗裡（貼著船殼、還看得到水的地方不畫）
        speck *= smoothstep(0.35, 0.85, a);
        col = mix(col, u._SpeckColor.rgb, clamp(speck * u._SpeckStrength, 0.0, 1.0));
    }
    outColor = vec4(col, a);
}
