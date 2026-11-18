#version 450

// DepthRim 海面背景層：畫在 DepthRim_OpenSea 格上（類虛空地形，本身不畫）。
// 頂點著色器沿用原版 MapWorldUV.vert.glsl（vUv = 世界座標 XZ）。
//
// 海面是平台下方 _Height.x 格的另一個平面。照 RimWorld 的斜俯視慣例（平台側面往南垂），越低的東西畫得越南：
// 地圖平面上的點 w 看到的海面座標 S = w + (0, _Height.x)。所以
//   - 平台的影子落在海上、整塊往南偏 _Height.x 格（再加一點日照方向的偏移），中間隔著支柱；
//   - 支柱與海面交界（支柱腳下的白沫）剛好在支柱貼圖的下緣；
//   - 平台邊緣本身不碰水，旁邊沒有白沫。
// 遮罩 _MaskTex 每格一個像素，C# 先模糊好（DepthRim.PlatformShadowField），這裡每處只取樣一次：
//   r = 高出海面的平台（模糊），g = 支柱入水處（模糊），b = 浮在水面的平台（船體平台，不模糊），a = 平台（大半徑模糊，天光遮蔽）。
// 浮在水面的平台的水線只在甲板下方 _Hull.x 格：影子短、緊貼船身，船身四周的水面有一圈白沫。
// 晝夜明暗交給原版的光照暗化層（Darkness 3180），這裡只畫白天的海。
// 2026-11-15：高出海面的平台造成的暗（天光遮蔽、陽光投影、柱影）由 DepthRim_PlatformShadow 蓋在上面（平台下的浮船、建築、Pawn 也一起變暗），
// 這裡只畫浮船自己的短影子；它和反光點跟著太陽（_Cast），被平台擋住太陽的地方沒有。
//
// 2026-11-14：海水本身改照原版深海（TerrainWater.frag.glsl ＋ WaterDepth.frag.glsl）的作法畫，看起來和原版的深海水一樣：
//   漣漪高度場（原版 Other/Ripples，四層不同方向、速度的取樣相乘）→ 法線 → 日月的漫射與反光強度 → 查原版深海的光照色階
//   （WaterOceanDeep 的 WaterDeepRamp：橫軸漫射、縱軸反光）→ 加雲的倒影與一點雜訊。
//   原版的高度場是另外一台攝影機畫到螢幕空間的貼圖，這裡沒有那張貼圖，所以直接在世界座標算高度，再用有限差分求法線。
//   原本自己做的配色（斑塊、浪峰線）留作 _Vanilla.x 以外的混合，浪峰線只留一點點。

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(0.055, 0.215, 0.300, 1.0)  海水底色
    vec4 _DeepColor;      // @color @default = vec4(0.030, 0.120, 0.190, 1.0)  大片的深色斑塊
    vec4 _CrestColor;     // @color @default = vec4(0.300, 0.520, 0.600, 1.0)  浪峰的亮面
    vec4 _FoamColor;      // @color @default = vec4(0.850, 0.920, 0.940, 1.0)  白沫
    vec4 _MapSize;        // @default = vec4(250.0, 250.0, 0.0, 0.0)          地圖大小（格）
    vec4 _Height;         // @default = vec4(14.1, 14.1, 1.0, 0.0)            x = 看下去海面往南偏幾格（平台層 = 甲板高，水面層 0），y = 甲板離海面幾格
    vec4 _Hull;           // @default = vec4(0.5, 0.45, 1.0, 0.0)             浮在水面的平台：x = 水線在甲板下幾格，y = 影子濃度，z = 水線白沫強度
    vec4 _Cast;           // @default = vec4(0.0, -1.75, 1.0, 0.0)            太陽（每幀）：xy = 甲板影子的位移（格），z = 影子強度（時刻、天氣、日夜）
    vec4 _Sway;           // @default = vec4(0.6, 0.35, 0.0, 0.0)             海水擺動：x = 左右幅度（格，已乘天氣倍率），y = 上下佔左右的比例
    float _GameSeconds;   // 動畫時鐘；全域
    float _WaveSpeed;     // @default = 1.0    浪的速度倍率
    float _FoamAmount;    // @default = 1.0    支柱腳下白沫的強度（0 = 沒有）
    float _GlintAmount;   // @default = 1.0    陽光反光點的強度
    vec4 _Vanilla;        // @default = vec4(1.0, 0.12, 1.0, 1.0)           x = 原版深海配色的比重，y = 浪峰線強度，z = 漣漪密度，w = 漣漪強度
    vec4 _FoamPhase;      // @default = vec4(12.0, 5.7, 0.01, 0.006)       浪花擺動的相位場：x = 雜訊圖一個像素幾格，y = 相位最多差幾秒，zw = 雜訊圖平移（像素／秒）
    vec4 _Rough;          // @default = vec4(0.0, 0.0, 0.0, 0.0)           海況（惡劣海況天氣，2026-11-17）：x = 開放海面的白浪，y = 海色變暗偏灰綠，z = 支柱與船身的浪花範圍加大
    vec4 _WaterCastVectSun;               // 全域（SkyManager）：.xz 太陽投影方向，.w 日光強度
    vec4 _WaterCastVectMoon;              // 全域（SkyManager）：.xz 月亮投影方向，.w 月光強度
    float _DayPercent;                    // 全域（SkyManager）
    float _LightsourceShineSizeReduction; // 全域（SkyManager）
    float _LightsourceShineIntensity;     // 全域（SkyManager）
} u;

layout(set = 2, binding = 1) uniform sampler2D _MaskTex; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _RippleTex;          // 原版漣漪（TexGame.RippleTex，Other/Ripples）
layout(set = 2, binding = 3) uniform sampler2D _RampTex;            // 原版深海的光照色階（WaterOceanDeep 的貼圖）
layout(set = 2, binding = 4) uniform sampler2D _WaterReflectionTex; // 原版雲的倒影（Other/WaterReflection）
layout(set = 2, binding = 5) uniform sampler2D _NoiseTex;           // 原版表面雜訊（TexGame.NoiseTex）

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

// 遮罩（世界座標 XZ，格）；地圖外一律 0。已經在 C# 模糊過，取樣一次就好
vec4 maskAt(vec2 world)
{
    vec2 uv = world / max(u._MapSize.xy, vec2(1.0));
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0)
        return vec4(0.0);
    return texture(_MaskTex, flipV(uv));
}

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
    float a = hash12(i);
    float b = hash12(i + vec2(1.0, 0.0));
    float c = hash12(i + vec2(0.0, 1.0));
    float d = hash12(i + vec2(1.0, 1.0));
    return mix(mix(a, b, w.x), mix(c, d, w.x), w.y);
}

// 每層 fbm 轉一個角度（約 37°）：值雜訊的等高線會沿著格子的橫線、直線走，各層對齊時門檻切出來的邊就是方的；
// 每層轉開，格子方向就不會疊在一起（2026-11-18：暴風雨的白浪出現方形邊）
const mat2 OCTAVE_ROT = mat2(0.8, 0.6, -0.6, 0.8);

float fbm(vec2 p)
{
    float v = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        v += valueNoise(p) * amp;
        p = OCTAVE_ROT * p * 2.03 + vec2(17.1, 9.2);
        amp *= 0.5;
    }
    return v;
}

vec2 hash22(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.xx + p3.yz) * p3.zy);
}

// 梯度雜訊（Perlin）：0..1，平均 0.5。等高線不會像值雜訊那樣沿格線拉成直線，拿來切門檻（白浪的生滅）邊緣才是不規則的
float gradientNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 w = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = dot(hash22(i) * 2.0 - 1.0, f);
    float b = dot(hash22(i + vec2(1.0, 0.0)) * 2.0 - 1.0, f - vec2(1.0, 0.0));
    float c = dot(hash22(i + vec2(0.0, 1.0)) * 2.0 - 1.0, f - vec2(0.0, 1.0));
    float d = dot(hash22(i + vec2(1.0, 1.0)) * 2.0 - 1.0, f - vec2(1.0, 1.0));
    return clamp(0.5 + mix(mix(a, b, w.x), mix(c, d, w.x), w.y), 0.0, 1.0);
}

// 脊狀雜訊：0..1，1 = 細長的浪峰線
float ridge(vec2 p)
{
    return 1.0 - abs(2.0 * valueNoise(p) - 1.0);
}

// ── 原版深海（WaterDepth.frag.glsl 的高度場、TerrainWater.frag.glsl 的打光）──────────────
const float WaterMovementSpeed = 0.00055;

// 原版的一層漣漪：旋轉後慢慢平移的漣漪貼圖。原版另有兩個時間相位的取樣混合，但那只在河流（有流向）時不同，
// 靜水時兩個取樣一樣，這裡省掉
float rippleLayer(vec2 originUv, vec2 phaseOffsets, float rotate, float t)
{
    mat2 rot = mat2(cos(rotate), sin(rotate), -sin(rotate), cos(rotate));
    vec2 uvRotated = rot * originUv + t * phaseOffsets * WaterMovementSpeed;
    return textureLod(_RippleTex, uvRotated * u._Vanilla.z * 15.0, 0.0).r;
}

// 漣漪高度（世界座標，格）：四層相乘，和原版一樣扣掉平均
float rippleHeight(vec2 world, float t)
{
    vec2 origin = world / max(u._MapSize.xy, vec2(1.0));
    float a = rippleLayer(origin, vec2(0.0, 5.0), 0.3, t);
    float b = rippleLayer(origin, vec2(2.1, 3.333), -0.35, t);
    float c = rippleLayer(origin, vec2(-2.3, 1.877), -0.8, t);
    float d = rippleLayer(origin, vec2(3.312, 0.444), 0.65, t);
    return (a * b * c * d - 0.0625) * u._Vanilla.w;
}

float waterDiffuseIntensity(vec3 normal, vec3 lightDirection)
{
    float current = dot(-lightDirection, normal);
    float vertical = dot(-lightDirection, vec3(0.0, 0.0, 1.0));
    if (current < vertical)
        return pow(max(current / vertical, 0.0), 100.0) * 0.5;
    return (1.0 - pow(1.0 - (current - vertical) / (1.0 - vertical), 5.0)) * 0.5 + 0.5;
}

float waterSpecularIntensity(vec3 normal, vec3 lightDirection, vec3 viewDirection)
{
    return pow(max(0.0, dot(reflect(-lightDirection, normal), viewDirection)), max(u._LightsourceShineSizeReduction, 1.0)) * u._LightsourceShineIntensity;
}

vec3 rampColor(float diffuse, float specular)
{
    return texture(_RampTex, vec2(clamp(diffuse, 0.0, 1.0), 1.0 - clamp(specular, 0.0, 1.0))).rgb;
}

// 原版深海在這一點的顏色（白天、沒有陰影時；晝夜明暗交給原版的光照暗化層）
vec3 vanillaDeepOcean(vec2 world, float t)
{
    // 法線：高度對世界座標的斜率（原版用螢幕空間的高度場；換算下來斜率倍率約 16 × 0.002）
    const float e = 0.12;
    float h0 = rippleHeight(world, t);
    float hx = rippleHeight(world + vec2(e, 0.0), t);
    float hz = rippleHeight(world + vec2(0.0, e), t);
    // 原版法線 = (螢幕上相鄰兩個半格取樣的高度差 ×2, 1 像素的世界長度 / 16 / 0.002)，換算成世界斜率的倍率是 0.064（和縮放無關）
    float k = 0.064 / e;
    vec3 normal = normalize(vec3((hx - h0) * k, -(hz - h0) * k, 1.0));

    vec3 viewDirection = vec3(0.0, 0.0, -1.0);
    vec3 castSun = normalize(vec3(-u._WaterCastVectSun.x, -u._WaterCastVectSun.z, -50.0));
    vec3 castMoon = normalize(vec3(-u._WaterCastVectMoon.x, -u._WaterCastVectMoon.z, -50.0));
    vec3 sunCol = rampColor(waterDiffuseIntensity(normal, castSun), waterSpecularIntensity(normal, castSun, viewDirection));
    vec3 moonCol = rampColor(waterDiffuseIntensity(normal, castMoon), waterSpecularIntensity(normal, castMoon, viewDirection));
    float moonW = u._WaterCastVectMoon.w;
    float sunW = u._WaterCastVectSun.w;
    vec3 lit = mix(sunCol, moonCol, moonW / max(moonW + sunW, 1e-4));
    float sunAmount = clamp(length(vec2(moonW, sunW)), 0.0, 1.0);
    vec3 col = mix(rampColor(0.0, 0.0), lit, sunAmount);

    // 雲的倒影
    vec2 uv = world / 16.0;
    float adjustedDay = 2.0 * mix(u._DayPercent, 1.0 - u._DayPercent, step(u._DayPercent, 0.5));
    float reflectionIntensity = mix(0.06, 0.04, adjustedDay);
    // 原版：(mix(uv / 16, 螢幕座標, 0.2) + 時間) × 3 + normal.xz × 10。這裡沒有螢幕座標，用約一個畫面寬（70 格）的世界座標代替那 20%，
    // 雲影才有原版那種一大片一大片的明暗
    vec2 reflectionUV = (mix(uv / 16.0, world / 70.0, 0.2) + t * 0.003) * 3.0 + normal.xz * 10.0;
    col += (1.0 - 2.0 * texture(_WaterReflectionTex, flipV(reflectionUV)).rgb) * reflectionIntensity;
    // 表面雜訊
    col = mix(col, col + texture(_NoiseTex, flipV(uv)).rgb - vec3(0.5), 0.025);
    return col;
}

// 海水的擺動（和 C# 的 DepthRim.SeaSway.Offset／Velocity 同一組式子）：左右為主、上下為輔，來回擺盪
vec2 swayOffset(float t)
{
    float x = sin(0.55 * t) + 0.3 * sin(1.37 * t + 1.3);
    float z = u._Sway.y * (sin(0.41 * t + 0.8) + 0.4 * sin(1.11 * t + 2.1));
    return vec2(x, z) * u._Sway.x;
}

vec2 swayVelocity(float t)
{
    float x = 0.55 * cos(0.55 * t) + 0.3 * 1.37 * cos(1.37 * t + 1.3);
    float z = u._Sway.y * (0.41 * cos(0.41 * t + 0.8) + 0.4 * 1.11 * cos(1.11 * t + 2.1));
    return vec2(x, z) * u._Sway.x;
}

// 雜訊圖（原版 _NoiseTex）當成平滑的值雜訊：p 以像素為單位，取四個像素用 smoothstep 權重內插。
// 直接 texelFetch，不靠貼圖的 mipmap 或解析度，一個像素可以放大成好幾格還是平滑的
float noiseMap(vec2 p)
{
    ivec2 size = max(textureSize(_NoiseTex, 0), ivec2(1));
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 w = f * f * (3.0 - 2.0 * f);
    ivec2 a = ivec2(i);
    ivec2 a0 = ((a % size) + size) % size;
    ivec2 a1 = (((a + 1) % size) + size) % size;
    float v00 = texelFetch(_NoiseTex, ivec2(a0.x, a0.y), 0).r;
    float v10 = texelFetch(_NoiseTex, ivec2(a1.x, a0.y), 0).r;
    float v01 = texelFetch(_NoiseTex, ivec2(a0.x, a1.y), 0).r;
    float v11 = texelFetch(_NoiseTex, ivec2(a1.x, a1.y), 0).r;
    return mix(mix(v00, v10, w.x), mix(v01, v11, w.x), w.y);
}

// 浪花擺動的時間相位（秒）（2026-11-15）：海水的擺動整片同步，浪花若也跟著同一個擺動，整片海的浪花同一時間只往一個方向擺。
// 這裡用平移中的雜訊圖給每個地方不同的相位：兩層往不同方向慢慢平移的雜訊相加，相位場跟著漂移、變形，
// 同一時間有的地方的浪花往東擺、有的往西，而且哪裡往哪擺會隨時間換。相位差最多 _FoamPhase.y 秒（約半個主擺動週期）
float foamPhase(vec2 world, float t)
{
    vec2 p = world / max(u._FoamPhase.x, 0.001);
    vec2 scroll = u._FoamPhase.zw * t;
    float n1 = noiseMap(p + scroll);
    float n2 = noiseMap(p * 1.9 + vec2(37.0, 11.0) + vec2(-scroll.y, scroll.x) * 1.3);
    return (n1 * 0.65 + n2 * 0.35) * u._FoamPhase.y;
}

void main()
{
    // 這個像素看到的海面座標（海面在平台下方，畫面上往南偏）
    vec2 world = vUv + vec2(0.0, u._Height.x);
    float t = u._GameSeconds * u._WaveSpeed;

    // 底色：大尺度的深淺斑塊，慢慢漂移
    float patches = fbm(world * 0.035 + vec2(t * 0.003, -t * 0.002));
    vec3 col = mix(u._DeepColor.rgb, u._Color.rgb, smoothstep(0.25, 0.75, patches));

    // 海水的擺動：花紋跟著位移來回擺（左右為主、上下為輔），另外加一點很慢的漂移，浪峰才不會在同一處來回
    vec2 sway = swayOffset(t);
    vec2 waterUv = world - sway - vec2(t * 0.06, 0.0);

    // 浪：浪峰是上下拉長的不規則線條（和左右的擺動方向垂直），再用低頻雜訊扭曲
    vec2 wind = normalize(vec2(1.0, 0.18));
    vec2 side = vec2(-wind.y, wind.x);
    vec2 q = vec2(dot(waterUv, wind) * 1.5, dot(waterUv, side) * 0.55);
    vec2 warp = vec2(fbm(q * 0.35 + vec2(t * 0.04, 0.0)), fbm(q * 0.35 + vec2(5.2, 1.3 - t * 0.03)));
    float r1 = ridge(q + warp * 1.8);
    float r2 = ridge(q * 2.3 + warp * 1.2 + vec2(13.0, 7.0) - vec2(sway.x * 0.8, 0.0));
    float crest = pow(r1, 5.0) * 0.6 + pow(r2, 6.0) * 0.4;
    // 原版深海的顏色（漣漪跟著海水一起擺）
    col = mix(col, vanillaDeepOcean(waterUv, t), u._Vanilla.x);
    col = mix(col, u._CrestColor.rgb, crest * u._Vanilla.y);

    // 海況：海色變暗、偏灰綠（暴風雨下的海），大片的明暗起伏也更明顯
    if (u._Rough.y > 0.0)
    {
        float lum = dot(col, vec3(0.299, 0.587, 0.114));
        vec3 stormy = vec3(lum) * vec3(0.78, 0.92, 0.95) * 0.72 + vec3(0.010, 0.030, 0.035);
        float swell = fbm(waterUv * 0.08 + vec2(t * 0.02, t * 0.013));
        stormy *= 0.80 + 0.40 * swell;
        col = mix(col, stormy, clamp(u._Rough.y, 0.0, 1.0));
    }

    // 平台擋住太陽的地方（只看甲板的投影；畫面上的暗由 DepthRim_PlatformShadow 畫）：那裡沒有反光點，也沒有浮船的短影子
    float sunBlocked = smoothstep(0.05, 0.85, maskAt(world - u._Cast.xy).r);
    float sunOn = clamp(u._Cast.z, 0.0, 1.0) * (1.0 - sunBlocked);

    // 浮在水面的平台（水面層的船體平台）：水線在它甲板下 _Hull.x 格，短影子沿太陽方向偏一點（最多 0.6 格）。
    // 水面層看海不偏移（_Height.x = 0）；平台層看下去時整片海（含船）都在 _Height.x 格下方
    vec2 hullWorld = world + vec2(0.0, u._Hull.x);
    vec2 hullCast = u._Cast.xy * 0.2;
    hullCast *= min(1.0, 0.6 / max(length(hullCast), 0.0001));
    float hullShade = smoothstep(0.05, 0.85, maskAt(hullWorld - hullCast).b) * u._Hull.y * sunOn;

    // 陽光反光點：浪峰上稀疏、閃爍的亮點；影子裡沒有
    vec2 g = waterUv * 4.0 + vec2(t * 0.05, 0.0);
    vec2 gid = floor(g);
    float gr = hash12(gid);
    float twinkle = 0.5 + 0.5 * sin(t * (2.0 + 3.0 * hash12(gid + 3.1)) + gr * 40.0);
    float spot = 1.0 - smoothstep(0.03, 0.10, length(fract(g) - 0.5));
    float glint = step(0.95, gr) * spot * twinkle * smoothstep(0.2, 0.6, crest) * sunOn;
    col += vec3(0.9, 0.95, 1.0) * glint * 0.7 * u._GlintAmount;

    col *= 1.0 - hullShade;

    // 支柱腳下：被浪打出來的一圈白沫，和拖在水流下游的尾流：水往回擺時尾流跟著換邊
    // （水往東流時，尾流在支柱東邊，所以從支柱往回看 = 取樣點往西移）
    // 浪花用這個地方自己的擺動相位（foamPhase）：同一時間各處的浪花往不同方向擺，不會整片一起左右晃
    float foamT = t + foamPhase(world, t);
    vec2 flow = swayVelocity(foamT);
    vec2 foamUv = world - swayOffset(foamT) - vec2(t * 0.06, 0.0);
    float flowSpeed = length(flow);
    vec2 downstream = flowSpeed > 0.001 ? flow / flowSpeed : vec2(1.0, 0.0);
    float flowShare = clamp(flowSpeed / max(u._Sway.x * 0.9, 0.001), 0.0, 1.0);
    float wakeLength = 1.0 + 2.2 * flowShare;
    float pillarNear = maskAt(world).g;
    float pillarWake = maskAt(world - downstream * wakeLength).g;
    float foamNoise = fbm(foamUv * 3.0 + vec2(t * 0.05, t * 0.04)) + crest * 0.2;
    // 海況：浪花的範圍與密度加大（_Rough.z）
    float ringLo = 0.02 * (1.0 - 0.7 * u._Rough.z);
    float ring = smoothstep(ringLo, 0.30 - 0.18 * u._Rough.z, pillarNear);
    float foam = smoothstep(0.75 - 0.35 * ring - 0.12 * u._Rough.z, 0.90 - 0.35 * ring - 0.12 * u._Rough.z, foamNoise) * ring;
    foam = max(foam, smoothstep(0.70 - 0.15 * u._Rough.z, 0.85 - 0.15 * u._Rough.z, foamNoise) * smoothstep(ringLo, 0.25, pillarWake) * (0.6 + 0.3 * u._Rough.z));
    col = mix(col, u._FoamColor.rgb * (1.0 - 0.45 * hullShade), clamp(foam * u._FoamAmount, 0.0, 1.0) * 0.8);

    // 海況：開放海面的白浪（碎浪的白頭）。沿著浪峰線出現，一陣一陣地生成、消散（life 隨時間漂移），
    // 再用細的浪花雜訊打碎成一條一條；有平台影子的地方一樣有
    if (u._Rough.x > 0.0)
    {
        // 生滅用梯度雜訊、兩層各轉不同角度：值雜訊在這裡切門檻會切出約 9 格長的筆直邊（方形的白浪區）
        float life = gradientNoise(OCTAVE_ROT * world * 0.11 + vec2(t * 0.17, -t * 0.11)) * 0.6
                   + gradientNoise(transpose(OCTAVE_ROT) * world * 0.37 - vec2(t * 0.29, t * 0.21)) * 0.4;
        float caps = smoothstep(0.30, 0.65, crest + 0.25 * fbm(foamUv * 0.6)) * smoothstep(0.62 - 0.22 * u._Rough.x, 0.80 - 0.15 * u._Rough.x, life);
        caps *= smoothstep(0.42, 0.68, foamNoise);
        // 被風吹開的細碎水沫：沿風向拉長的淡淡條紋
        float streak = ridge(vec2(dot(foamUv, wind) * 0.35, dot(foamUv, side) * 3.2) + vec2(t * 0.08, 0.0));
        float spray = pow(streak, 8.0) * smoothstep(0.45, 0.75, life) * 0.35;
        col = mix(col, u._FoamColor.rgb * (1.0 - 0.45 * hullShade), clamp((caps * 0.9 + spray) * u._Rough.x, 0.0, 0.9));
    }

    // 船身四周的水線白沫：只在船身外緣一圈（船身底下被甲板蓋住，不用管）
    // 下游那一側的白沫多一點：取樣往上游偏。海況越差，浪打上船身的白沫圈越寬（往外多取樣四點）
    float hullNear = maskAt(hullWorld - downstream * 0.35 * flowShare).b;
    if (u._Rough.z > 0.0)
    {
        float reach = 1.4 * u._Rough.z;
        float wide = max(max(maskAt(hullWorld + vec2(reach, 0.0)).b, maskAt(hullWorld - vec2(reach, 0.0)).b),
                         max(maskAt(hullWorld + vec2(0.0, reach)).b, maskAt(hullWorld - vec2(0.0, reach)).b));
        hullNear = max(hullNear, wide * 0.5 * (0.6 + 0.4 * foamNoise));
    }
    float hullRing = smoothstep(0.03, 0.22, hullNear) * (1.0 - smoothstep(0.55, 0.9, hullNear));
    float hullFoam = smoothstep(0.62, 0.80, foamNoise + 0.15 * hullRing) * hullRing;
    col = mix(col, u._FoamColor.rgb * (1.0 - 0.45 * hullShade), clamp(hullFoam * u._Hull.z * u._FoamAmount, 0.0, 1.0) * 0.7);

    outColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
