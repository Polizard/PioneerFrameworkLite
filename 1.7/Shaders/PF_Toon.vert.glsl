#version 450

// PF_Toon.vert.glsl — 船模/编队浮雕的三渲二材质（2026-10-15，1.7 迁移 P2-e）。
//
// 保真移植自 1.6 的 Unity 版 `Shaders/PF_Toon.shader`（V-1.4 四阶量化）。1.6 = ShaderLab
// 单材质双 pass（OUTLINE + TOON）经 AssetBundle 交付；1.7 的 Waylith 管线**一个 ShaderDef
// 只有一个 pass**，且 bundle 通道只认 Vulkan/SPIR-V（`_win` 包作废）⇒ 描边拆成独立
// ShaderDef `PF_ToonOutline`（`cullMode=Front` + 沿法线挤出），由 C# 逐部件补第二笔 draw。
//
// 顶点绑定约定照抄 `PFLit.vert.glsl`（引擎绑定与"法线/太阳同系"约定见该文件头注）。

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
