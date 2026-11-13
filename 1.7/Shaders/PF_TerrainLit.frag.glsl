#version 450

// PF_TerrainLit.frag.glsl — 逐格地形专用受光（2026-11-09，目标 A：近景地形起伏可读）。
// 与 PFLit 同一套昼夜口径（照抄原版 WorldTerrain 的 day/night 混色），额外叠一个**地形浮雕项**：
//   法线来自 C# 位移后的地形网格 ⇒ 山脊/坑缘/河床沿太阳方向出现明暗，而不是只有晨昏线。
// `_Terrain.x` = 浮雕强度（0 = 与 PFLit 完全一致；地形材质设 1）。

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _SunDir;     // @default = vec4(1.0, 0.0, 0.0, 0.0)
    vec4 _SunColor;   // @default = vec4(1.0, 0.98, 0.9, 1.0)
    vec4 _Emission;   // @default = vec4(0.0, 0.0, 0.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _Terrain;    // @default = vec4(1.0, 0.0, 0.0, 0.0)  x = 浮雕强度
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

const vec3  NightColor    = vec3(0.482, 0.603, 0.682);
const vec3  DayColor      = vec3(1.0, 1.0, 1.0);
const float DayNightStart = 0.45;
const float DayNightEnd   = 0.55;

vec3 sunDiffuseLight(vec3 normal, vec3 toSun)
{
    if (dot(toSun, toSun) < 1e-6)
        return DayColor;
    float incidence = dot(normal, normalize(toSun)) * 0.5 + 0.5;
    float t = (incidence - DayNightStart) / (DayNightEnd - DayNightStart);
    return mix(NightColor, DayColor, clamp(t, 0.0, 1.0));
}

void main()
{
    vec3 n = normalize(vNormal);
    vec3 dayNight = sunDiffuseLight(n, uMaterial._SunDir.xyz) * uMaterial._SunColor.rgb;
    vec4 tex = texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);

    // 浮雕：半兰伯特（0.5+0.5·N·L）把法线倾斜变成明暗；强度 0 时严格退回 PFLit 口径。
    float relief = 1.0;
    if (uMaterial._Terrain.x > 0.001)
    {
        float ndl = dot(n, normalize(uMaterial._SunDir.xyz)) * 0.5 + 0.5;
        relief = mix(1.0, 0.50 + 0.95 * ndl, uMaterial._Terrain.x);
    }

    vec3 color = (uMaterial._Color.rgb * tex.rgb * dayNight + uMaterial._Emission.rgb * tex.rgb) * relief;
    outColor = vec4(color, uMaterial._Color.a * tex.a);
}