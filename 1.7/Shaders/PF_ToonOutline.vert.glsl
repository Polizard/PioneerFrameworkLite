#version 450

// PF_ToonOutline.vert.glsl — 描边：inverted hull（沿法线挤出 + 引擎侧 `cullMode=Front` 只画背面）。
// 保真移植自 1.6 `Shaders/PF_Toon.shader` 的 OUTLINE pass——挤出在**模型空间**做（随模型缩放），
// 与 1.6 原文一致：`extruded = v.vertex.xyz + normalize(v.normal) * _OutlineWidth`。
//
// ⚠ 顶点阶段也读材质块（C# 逐部件喂 `_OutlineWidth`）；与 frag 的块**成员与顺序必须一致**
//   （Vulkan 同 set/binding 的布局跨阶段唯一）。原版先例：`Map/BuildingSquashNStretch.vert.glsl`。

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4  _OutlineColor;    // @color @default = vec4(0.0, 0.0, 0.0, 1.0)
    float _OutlineWidth;    // @default = 0.03
} uMaterial;

void main()
{
    vec3 n = normalize(inNormal);
    vec4 world = push.modelMatrix * vec4(inPosition + n * uMaterial._OutlineWidth, 1.0);
    gl_Position = uVertex.ViewProj * world;
}
