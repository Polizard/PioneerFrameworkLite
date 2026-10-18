#version 450

// PF_RingLit.frag.glsl — 环世界「电影感」受光主体：片元阶段（IMPL §2.3，2026-10-17，recon-render/task-5）。
//
// 光照口径**照抄原版世界地表**（Data/Core/Shaders/World/WorldTerrain.frag.glsl:62-69，勿改数值）：
//   卡通昼夜混色，不是物理 Lambert：
//   lightInc = 1 - (dot(N, 光传播方向)*0.5 + 0.5)；在 0.45→0.55 过渡；
//   夜面 (0.482,0.603,0.682) → 昼面 (1,1,1)。
// `_RingSunDir.xyz` = **光传播方向**（太阳→环，世界系；C# 每帧写，这里再归一化并做零长度兜底）。
//
// 电影感追加项（契约固定公式）：
//   ① 克制高光：Blinn-Phong 幂 24 × _RingParams.w；
//   ② 边缘光：pow(1-|dot(N,V)|, _RingParams.y) × _RingParams.x × _RimColor；
//   ③ 自发光：_EmissionTex × _EmissionColor.rgb × _EmissionColor.a × _RingParams.z（不需要时置 0）。
//
// 贴图采样：uv * _MainTex_ST.zw + _MainTex_ST.xy。PF_RingLit 的 ShaderDef 已显式
// `<flipTextureST>false</flipTextureST>`（Lead 裁决 b）⇒ raw UV、PNG 顶部 = 环 +Y 侧。
//
// 注：`_RingTime` 按 IMPL §2.3 Lit 材质块声明（C# 共享 uniform 写入器统一写），本 shader 不参与动画，
//     属"声明保留槽"，无功能影响（引擎写入未使用的 uniform 无害）。

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec4 vColor;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _RingParams;    // @default = vec4(0.35, 2.5, 0.0, 0.12)  x=rim 强度 y=rim 幂 z=自发光倍率 w=高光强度
    vec4 _EmissionColor; // @color @default = vec4(0.0, 0.0, 0.0, 0.0)  rgb=自发光色 a=总强度
    vec4 _RimColor;      // @color @default = vec4(0.45, 0.62, 0.95, 1.0)
    vec4 _RingSunDir;    // @default = vec4(0.3, 0.75, -0.55, 0.0)  xyz=光传播方向（太阳→环，世界系）
    vec4 _RingCameraPos; // @default = vec4(0.0, 0.0, 0.0, 1.0)     xyz=世界相机位置
    vec4 _RingTime;      // @default = vec4(0.0, 0.0, 0.0, 0.0)     x=秒（声明保留；本 shader 不使用）
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _EmissionTex;

// ── 原版世界地表的昼夜常数（照抄，勿改；改 = 偏离已认可的观感口径）──
const vec3  NightColor    = vec3(0.482, 0.603, 0.682);
const vec3  DayColor      = vec3(1.0, 1.0, 1.0);
const float DayNightStart = 0.45;
const float DayNightEnd   = 0.55;
const float SpecPower     = 24.0;

void main()
{
    // ── 法线（世界系；零长度兜底 +Y，避免 normalize(0) 产生 NaN）──
    vec3 N = vNormal;
    float nLen = length(N);
    N = (nLen > 1e-6) ? (N / nLen) : vec3(0.0, 1.0, 0.0);

    // ── 太阳方向（光传播方向；未喂时用契约兜底方向）──
    vec3 sunDir = uMaterial._RingSunDir.xyz;
    float sunLen = length(sunDir);
    sunDir = (sunLen > 1e-6) ? (sunDir / sunLen) : normalize(vec3(0.3, 0.75, -0.55));

    // ── 原版昼夜混色（与 WorldTerrain.frag.glsl 的 sunDiffuseLight 同公式）──
    float lightInc = 1.0 - (dot(N, sunDir) * 0.5 + 0.5);
    float t = clamp((lightInc - DayNightStart) / (DayNightEnd - DayNightStart), 0.0, 1.0);
    vec3 dayNight = mix(NightColor, DayColor, t);

    // ── albedo（_MainTex_ST 采样；flipTextureST=false ⇒ raw UV 方向）──
    vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
    vec3 albedo = texture(_MainTex, uv).rgb * uMaterial._Color.rgb * vColor.rgb;
    vec3 color = albedo * dayNight;

    // ── 电影感①：克制高光（Blinn-Phong；H 用"指向太阳"= -光传播方向）──
    vec3 toCamera = uMaterial._RingCameraPos.xyz - vWorldPos;
    float camDist = length(toCamera);
    vec3 V = (camDist > 1e-6) ? (toCamera / camDist) : vec3(0.0, 0.0, 1.0);
    vec3 H = normalize(-sunDir + V);
    float spec = pow(max(dot(N, H), 0.0), SpecPower) * max(uMaterial._RingParams.w, 0.0);
    color += vec3(spec) * dayNight;

    // ── 电影感②：边缘光（V 与 N 越垂直越亮；幂下限防 0/负指数）──
    float rimExp = max(uMaterial._RingParams.y, 1e-3);
    float rim = pow(clamp(1.0 - abs(dot(N, V)), 0.0, 1.0), rimExp) * max(uMaterial._RingParams.x, 0.0);
    color += uMaterial._RimColor.rgb * rim;

    // ── 电影感③：自发光（黑底贴图；z/a 置 0 即关闭）──
    vec3 emission = texture(_EmissionTex, uv).rgb * uMaterial._EmissionColor.rgb
                  * uMaterial._EmissionColor.a * max(uMaterial._RingParams.z, 0.0);
    color += emission;

    outColor = vec4(color, 1.0);
}
