#version 450

// DepthRim 平台層往下看的快照（水面層的浮船與建築）：一般的透明貼圖，ShaderDef 排在 1940：
// 海面（1900）之後、支柱柱身（1950）之前，所以浮船在柱身後面；再被平台層的甲板（2000）蓋住。頂點著色器沿用原版 Transparent.vert.glsl。

#ifdef INSTANCED
#include "InstanceTransforms.glsl"
#endif

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    outColor = texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy) * uMaterial._Color * vColor;
}
