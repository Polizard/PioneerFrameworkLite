#version 450

// PF_RingGlow.vert.glsl — 环世界「电影感」加性辉光：顶点阶段（IMPL §2.2，2026-10-17，recon-render/task-5）。
//
// 顶点约定与 PF_RingLit.vert.glsl / PFLit.vert.glsl 完全一致（location 不可改）：
//   0 = Position(required)，1 = Normal(optional)，2 = Color(optional)，3 = TexCoord0(optional)。
// 辉光模式 0(rim) 需要世界法线、模式 1(ribbon) 需要 uv.y、模式 2(sprite) 需要 uv/顶点色 a = 相位。
// 法线经 mat3(modelMatrix) 变到世界系；不依赖 FragmentUniforms（相机位置由 material uniform 给）。

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)
layout(location = 2) in vec4 inColor;    // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0;      // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec4 vColor;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;

    vNormal = mat3(push.modelMatrix) * inNormal;
    vUv = inUv0.xy;
    vWorldPos = world.xyz;
    vColor = inColor;
}
