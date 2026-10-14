#version 450

// PFLit.vert.glsl — PioneerFramework 通用受光材质（2026-10-13，1.7 迁移 P2-c）。
// 用途：替换 1.7 上不可用的 `Shader.Find("Standard")`（UnityStubs 引擎注册表里没有 Standard）。
// 顶点绑定约定照抄原版 `Rendering/Shaders/SceneDefault.vert.glsl`：
//   0 = Position(required)，1 = Normal(optional)，2 = Color(optional)，3 = TexCoord0(optional)。

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)
layout(location = 3) in vec4 inUv0;      // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorldPos;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;

    vNormal = inNormal;
    vUv = inUv0.xy;
    vWorldPos = world.xyz;
}
