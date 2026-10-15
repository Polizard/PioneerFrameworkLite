#version 450

// PF_ToonOutline.frag.glsl — 纯色描边（1.6 OUTLINE pass 的 frag 原文即 `return _OutlineColor;`）。
// 块成员与 `PF_ToonOutline.vert.glsl` 必须逐字一致（见该文件头注）。

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4  _OutlineColor;    // @color @default = vec4(0.0, 0.0, 0.0, 1.0)
    float _OutlineWidth;    // @default = 0.03
} uMaterial;

void main()
{
    outColor = uMaterial._OutlineColor;
}
