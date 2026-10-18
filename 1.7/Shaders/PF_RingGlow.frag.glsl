#version 450

// PF_RingGlow.frag.glsl — 环世界「电影感」加性辉光：片元阶段（IMPL §2.3 modes 0/1/2，2026-10-17，recon-render/task-5）。
//
// 统一口径：additive、depthWrite=false、无光照（不做昼夜混色）、不进 bloom。
// 模式由 `_GlowParams.x` 选择（C# 置 0/1/2）：
//   mode 0 rim    : pow(1-|dot(N,V)|, z) * y * vColor.rgb * _GlowColor.rgb
//   mode 1 ribbon : pow(1-|uv.y*2-1|, z) * y * vColor.rgb * _GlowColor.rgb * 昼夜因子(0.65..1)
//   mode 2 sprite : 径向衰减 (1-d)^z * 脉冲 * vColor.rgb * _GlowColor.rgb * _GlowTex 柔和遮罩
// 太阳方向 `_RingSunDir.xyz` = 光传播方向（世界系）；相机位置 `_RingCameraPos.xyz` = 世界相机位置；
// 时间 `_RingTime.x` = 秒。全部由 C# RingWorldVisualState 每帧写，不依赖 FragmentUniforms。
//
// 贴图：`_GlowTex`（binding=1，柔和径向点图，PNG RGBA）。未绑定时引擎默认白图 ⇒ 采样值 1.0，
// 模式 2 的 softDot 只做额外软衰减、不改变基形。ShaderDef 显式 `<flipTextureST>false</flipTextureST>`
// （Lead 裁决 b）⇒ raw UV；但本 shader 的 sprite UV 是 C# 生成的 0..1 正方形 UV，与 PNG 上下方向无关。

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec4 vColor;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _GlowColor;     // @color @default = vec4(0.45, 0.68, 1.0, 1.0)
    vec4 _GlowParams;    // @default = vec4(0.0, 1.0, 2.0, 0.0)  x=mode(0 rim/1 ribbon/2 sprite/3 terminal) y=强度 z=falloff 幂 w=脉冲角速度
    vec4 _RingSunDir;    // @default = vec4(0.3, 0.75, -0.55, 0.0)  xyz=光传播方向（太阳→环，世界系）
    vec4 _RingCameraPos; // @default = vec4(0.0, 0.0, 0.0, 1.0)     xyz=世界相机位置
    vec4 _RingTime;      // @default = vec4(0.0, 0.0, 0.0, 0.0)     x=秒（sprite 脉冲动画）
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _GlowTex;

const float TwoPi = 6.28318530718;

float terminalPulse()
{
    float breath = 0.5 + 0.5 * sin(uMaterial._RingTime.x * uMaterial._GlowParams.w + vColor.a * TwoPi);
    return 0.08 + 0.92 * breath * breath;
}

void main()
{
    // ── 公共量：世界法线 / 视线方向 / 光传播方向（均做零长度兜底，避免 NaN）──
    vec3 N = vNormal;
    float nLen = length(N);
    N = (nLen > 1e-6) ? (N / nLen) : vec3(0.0, 1.0, 0.0);

    vec3 toCamera = uMaterial._RingCameraPos.xyz - vWorldPos;
    float camDist = length(toCamera);
    vec3 V = (camDist > 1e-6) ? (toCamera / camDist) : vec3(0.0, 0.0, 1.0);

    vec3 sunDir = uMaterial._RingSunDir.xyz;
    float sunLen = length(sunDir);
    sunDir = (sunLen > 1e-6) ? (sunDir / sunLen) : normalize(vec3(0.3, 0.75, -0.55));

    vec3 base = vColor.rgb * uMaterial._GlowColor.rgb;
    float intensity = max(uMaterial._GlowParams.y, 0.0);
    float falloff = max(uMaterial._GlowParams.z, 1e-3);
    int mode = int(uMaterial._GlowParams.x + 0.5);

    vec3 color = vec3(0.0);

    if (mode == 1)
    {
        // ── mode 1 ribbon：大气薄带/环带光环几何（UV.y 跨带归一化）──
        float ribbon = pow(clamp(1.0 - abs(vUv.y * 2.0 - 1.0), 0.0, 1.0), falloff);
        float lightInc = 1.0 - (dot(N, sunDir) * 0.5 + 0.5);
        float dayT = clamp((lightInc - 0.45) / 0.10, 0.0, 1.0);
        float dayNight = mix(0.65, 1.0, dayT);
        color = base * ribbon * intensity * dayNight;
    }
    else if (mode == 4)
    {
        // 实体灯头整个表面发光，不受视角、昼夜或柔光贴图衰减。
        color = base * terminalPulse() * intensity;
    }
    else if (mode == 3)
    {
        // 垂直构件末端：周期由材质参数给出，保留少量底光；alpha 提供每构件固定相位。
        float d = length(vUv - vec2(0.5)) * 2.0;
        float radial = max(1.0 - smoothstep(0.25, 0.65, d), 0.45 * pow(max(0.0, 1.0 - d), falloff));
        color = base * radial * terminalPulse() * intensity;
    }
    else if (mode == 2)
    {
        // ── mode 2 sprite：警示灯光晕/漂浮信标（vColor.a = 每盏灯相位 0..1）──
        float d = length(vUv - vec2(0.5)) * 2.0;
        float falloffRadial = pow(max(0.0, 1.0 - d), falloff);
        float pulse = 0.7 + 0.3 * sin(uMaterial._RingTime.x * uMaterial._GlowParams.w + vColor.a * TwoPi);
        float softDot = texture(_GlowTex, vUv).r;   // 未绑定 = 白图 (1.0)，只额外软衰减
        color = base * falloffRadial * pulse * intensity * softDot;
    }
    else
    {
        // ── mode 0 rim：外壳边缘辉光（N·V 越垂直越亮）──
        float rim = pow(clamp(1.0 - abs(dot(N, V)), 0.0, 1.0), falloff);
        color = base * rim * intensity;
    }

    // additive：alpha 不参与混合权重，写 1.0 保持口径简单（引擎 BlendState.Additive）
    outColor = vec4(color, 1.0);
}
