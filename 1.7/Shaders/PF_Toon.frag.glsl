#version 450

// PF_Toon.frag.glsl — 见 `PF_Toon.vert.glsl` 头注（P2-e，保真移植 V-1.4）。
//
// V-1.4 口径（**逐行照抄 1.6，勿改数值**）：半兰伯特 → 重映射到 [_ShadowThreshold, 1]
// → 量化成 `_ShadowSteps` 级阶梯（级内纯色 = 赛璐璐，级间 `_ShadowSmoothness` 软化抗锯齿）
// + `_Color` 的 0.03 环境项。1.6 原文出处：`Shaders/PF_Toon.shader` 的 TOON pass。
//
// ⚠ 两处 1.6→1.7 的**口径差异**（引擎面所限，非观感选择；已在交接件 §5 登记）：
//   ① 光照方向：1.6 用 `_WorldSpaceLightPos0`；1.7 用 C# 每帧喂的 `_SunDir`（指向太阳，
//      与 PFLit 同源同系——`dot(N, toSun)*0.5+0.5` 与 1.6 的 `dot(N, lightDir)*0.5+0.5` 同义）。
//   ② 阴影衰减：1.6 有 `UNITY_LIGHT_ATTENUATION`（接阴影贴图）；1.7 管线无阴影贴图 ⇒ 常数 1。

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4  _Color;            // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4  _ShadowColor;      // @default = vec4(0.15, 0.15, 0.2, 1.0)
    vec4  _SunDir;           // @default = vec4(1.0, 0.0, 0.0, 0.0)  xyz = 指向太阳的单位向量
    float _ShadowThreshold;  // @default = 0.05
    float _ShadowSmoothness; // @default = 0.06
    float _ShadowSteps;      // @default = 4.0
} uMaterial;

void main()
{
    // 太阳未喂（长度≈0）⇒ 全亮口径（与 PFLit 的"无太阳 ⇒ DayColor"一致）
    if (dot(uMaterial._SunDir.xyz, uMaterial._SunDir.xyz) < 1e-6)
    {
        outColor = vec4(uMaterial._Color.rgb * 1.03, 1.0);
        return;
    }

    vec3 n = normalize(vNormal);

    // 半兰伯特 [0, 1]（1.6 原文：dot(normal, lightDir) * 0.5 + 0.5）
    float halfLambert = dot(n, normalize(uMaterial._SunDir.xyz)) * 0.5 + 0.5;

    // ── V-1.4 阶梯量化（逐行照抄 1.6 frag，勿改）──
    float span = max(1e-4, 1.0 - uMaterial._ShadowThreshold);
    float t = clamp((halfLambert - uMaterial._ShadowThreshold) / span, 0.0, 1.0);

    float levels = max(2.0, uMaterial._ShadowSteps);
    float scaled = t * (levels - 1.0);
    float bandIndex = floor(scaled);
    float bandFrac = scaled - bandIndex;
    float bandBlend = smoothstep(0.5 - uMaterial._ShadowSmoothness, 0.5 + uMaterial._ShadowSmoothness, bandFrac);
    float toon = (bandIndex + bandBlend) / (levels - 1.0);

    // 暗面 → 亮面：阶梯式渐变 + 微弱环境光（避免暗面全黑）
    vec3 col = mix(uMaterial._ShadowColor.rgb, uMaterial._Color.rgb, toon);
    col += uMaterial._Color.rgb * 0.03;

    outColor = vec4(col, 1.0);
}
