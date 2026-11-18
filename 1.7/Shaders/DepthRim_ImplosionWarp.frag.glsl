#version 450
// DepthRim：原版 MoteLargeDistortionWave 的複本（靈能扭曲），只把抓畫面的時間點改到外海層與內爆之後（ShaderDef DepthRim_ImplosionWarp），
// 這樣扭曲的是水與內爆本身，而不是水底下看不到的地方。

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;              // @default = 0.0
    float _distortionIntensity;  // @default = 0.04
    float _brightnessMultiplier; // @default = 1.0
    float _noiseIntensity;       // @default = 2.0
    float _ageToNoiseRatio;      // @default = 100.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _DistortionTex; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _NoiseTex; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _GrabDepthRimImplosion;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    vec2 dir = normalize(centeredOffset);

    float distortion = texture(_DistortionTex, flipV(vUv)).x * u._distortionIntensity;
    float noiseCoord = atan(-dir.y, dir.x) * 3.0 + u._AgeSecs * u._ageToNoiseRatio;
    distortion *= 1.0 + texture(_NoiseTex, flipV(vec2(noiseCoord))).x * u._noiseIntensity;

    if (distortion - 0.001 < 0.0)
        discard;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDepthRimImplosion, 0));
    vec2 offset = centeredOffset * distortion * u._Color.a;
    vec4 bgcolor = texture(_GrabDepthRimImplosion, grabUv - vec2(offset.x, -offset.y));
    outColor = mix(bgcolor, bgcolor * u._brightnessMultiplier, u._Color.a * distortion);
}
