#version 450

// PF_ShipSkin.vert.glsl —— 舰船"极简硬表面"蒙皮（2026-10-19，用户裁定：只做板缝/倒角/阵营色）。
//
// 设计口径：
//   · **不改几何/不加 UV**：表面细节全部由片元阶段的三平面投影完成 ⇒ 现有盒体/地图浮雕网格
//     （都没有 UV）可以直接套同一层蒙皮；
//   · `vSkinPos` = 模型矩阵的"旋转+缩放"部分 × 局部位置（**去掉平移**）——
//     这样浮动原点/相机移动不会让板缝在船体上游动；单位 = 视图单位（米×MetersToView）；
//   · 顶点绑定沿用 PFLit/原版约定（location 0/1/3），法线/位置语义与 PFLit 一致。

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)
layout(location = 3) in vec4 inUv0;      // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)（本 shader 不用）

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec3 vSkinPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec3 vWorldPos;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;

    // 去掉平移（浮动原点），保留旋转/缩放 ⇒ 板缝贴在船体上、不随相机漂移。
    vSkinPos = world.xyz - push.modelMatrix[3].xyz;
    vNormal = mat3(push.modelMatrix) * inNormal;
    vWorldPos = world.xyz;
}
