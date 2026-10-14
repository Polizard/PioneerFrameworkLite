#version 450

// PFLit.frag.glsl — 见 PFLit.vert.glsl 头注（P2-c）。
//
// ⭐ 光照口径（2026-10-14 重写，**照抄原版世界地表** `Data/Core/Shaders/World/WorldTerrain.frag.glsl`
//    的 `sunDiffuseLight`）：原版行星**不是物理 Lambert**，而是**昼夜色混合**——
//      白天 = 贴图满亮度 `(1,1,1)`；夜面 = `(0.482, 0.603, 0.682)`（仍有 ~50-68% 亮度）；
//      过档宽度 0.45→0.55（近似硬切的晨昏线）。
//    旧版（本 shader 的前一稿）把白天压到 ~0.55、夜面压到 ~0.008 ⇒ 用户口径："**明亮的太阳光**"。
//    配套：反照率 `_Color` 从 0.20 改为 1.0（1.6 时代是"0.2 反照率 × 强度 5 = 乘积 1.0"的配方，
//    原版模型里乘积直接是 1.0）。
//
// `_SunDir` 沿用本模约定 = **指向太阳**；原版 `uFragment.SunDir` 是光**传播**方向（= 其反向），
// 符号已在下文 `incidence` 处换算好。

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _SunDir;     // @default = vec4(1.0, 0.0, 0.0, 0.0)  xyz = **指向太阳**的单位向量（视图系）
    vec4 _SunColor;   // @default = vec4(1.0, 0.98, 0.9, 1.0)  rgb = 太阳灯色（C# 每帧喂；**不含强度**——白天就是满亮度）
    vec4 _Emission;   // @default = vec4(0.0, 0.0, 0.0, 1.0)   rgb = 自发光系数（新模型下几乎无感，留作旋钮）
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

// ── 原版世界地表的昼夜常数（**照抄，勿改**；改 = 偏离已认可的观感口径）──
const vec3  NightColor    = vec3(0.482, 0.603, 0.682);
const vec3  DayColor      = vec3(1.0, 1.0, 1.0);
const float DayNightStart = 0.45;
const float DayNightEnd   = 0.55;

vec3 sunDiffuseLight(vec3 normal, vec3 toSun)
{
    if (dot(toSun, toSun) < 1e-6)
        return DayColor;

    // 原版：light = 0.5 - 0.5·dot(N, 光传播方向)，光传播方向 = -toSun
    //   ⇒ 本模"指向太阳"约定下等价于：incidence = 0.5 + 0.5·dot(N, toSun)（1 = 正对太阳）
    float incidence = dot(normal, normalize(toSun)) * 0.5 + 0.5;
    float t = (incidence - DayNightStart) / (DayNightEnd - DayNightStart);
    return mix(NightColor, DayColor, clamp(t, 0.0, 1.0));
}

void main()
{
    vec3 n = normalize(vNormal);
    vec3 dayNight = sunDiffuseLight(n, uMaterial._SunDir.xyz) * uMaterial._SunColor.rgb;
    // ⚠⚠ 2026-10-14（"烘焙结果左右反"的真根因）：**必须经 `_MainTex_ST` 采样**——
    //   引擎对 Unity 贴图（底朝上）的 V 翻正在 ST 里做（`ResolveTextureST`：`y=1-y; w=-w`），
    //   并且 `Material.mainTextureScale` 也走 ST。裸 `vUv` 采样 = 少翻一次 V（本模补丁的 V 轴=东西向
    //   ⇒ 症状恰是"左右反"），同时静默丢掉基底球的 6×3 平铺。原版 shader 全是这个写法。
    vec4 tex = texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
    vec3 color = uMaterial._Color.rgb * tex.rgb * dayNight + uMaterial._Emission.rgb * tex.rgb;
    outColor = vec4(color, uMaterial._Color.a * tex.a);
}
