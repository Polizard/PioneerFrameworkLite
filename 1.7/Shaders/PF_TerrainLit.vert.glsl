#version 450

// PF_TerrainLit.vert.glsl — 逐格地形专用顶点阶段（2026-11-09，目标 A）。
// 与 PFLit.vert 完全同构；**顶点语义注解是 Waylith ShaderDef 注册的硬要求**
//（漏了会报 "vertex input 'inPosition' is missing a @vertex annotation" 并让 Shader 注册失败）。

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