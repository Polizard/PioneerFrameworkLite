#version 450

// PF_RingLit.vert.glsl — 环世界「电影感」受光主体：顶点阶段（IMPL §2.2，2026-10-17，recon-render/task-5）。
//
// 顶点约定（与 PFLit.vert.glsl / 引擎 SceneDefault.vert.glsl 同源，切勿改 location）：
//   0 = Position(required)，1 = Normal(optional)，2 = Color(optional)，3 = TexCoord0(optional)。
// 世界层网格由 LayerSubMesh.normals 提供真法线（FinalizeMesh(MeshParts.All) 会 SetNormals）；
// 法线经 mat3(modelMatrix) 变到世界系（环模型矩阵只有旋转+平移、无缩放；frag 内再归一化）。
//
// ⚠ 不依赖 FragmentUniforms（set=0,binding=1）：该块是否对 Mod shader 自动绑定未验证。
//   相机位置/太阳方向/时间全部来自 material uniform（C# RingWorldVisualState 每帧写）。

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
