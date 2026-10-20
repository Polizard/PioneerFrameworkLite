#version 450

// PF_ShipSkin.frag.glsl —— 舰船"极简硬表面"蒙皮（2026-10-19）。
//
// 口径（用户裁定）：只做**板缝 + 倒角 + 阵营色**，不画舱窗/发光带/铆钉/做旧。
//   · 三平面投影采一张中性灰、四边无缝的装甲板贴图（`ShipSkinPlates.png`），
//     现有网格没有 UV 也能覆盖（盒体/地图浮雕共用同一套规则）；
//   · 受光口径与 PFLit **完全一致**（半兰伯特 + 两段昼夜混色 + `_SunColor`），
//     所以蒙皮后的船与行星/其它物体在同一盏太阳下；
//   · 阵营色 = `_Color`（C# 按阵营算好，乘在灰底上）；倒角亮棱已烘在贴图里（对称、无方向光），
//     另加一条很克制的轮廓 rim（silhouette 分离用，不发光）。

layout(location = 0) in vec3 vSkinPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)   阵营 tint（乘在灰底贴图上）
    vec4 _SunDir;     // @default = vec4(1.0, 0.0, 0.0, 0.0)          xyz = 指向太阳（视图系单位向量）
    vec4 _SunColor;   // @default = vec4(1.0, 0.98, 0.9, 1.0)         太阳灯色（不含强度）
    vec4 _CamPos;     // @default = vec4(0.0, 0.0, 0.0, 1.0)          视图系相机位置（rim 用）
    vec4 _SkinParams; // @default = vec4(0.625, 1.0, 0.18, 0.35)      x=每视图单位的贴图重复数 y=板面细节保真 z=rim 强度 w=几何边亮棱强度
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _SkinTex;

// 与 PFLit 同源的昼夜混色常量（照抄，别改：改了就与行星光照分家）
const vec3 NightColor = vec3(0.482, 0.603, 0.682);
const vec3 DayColor = vec3(1.0, 1.0, 1.0);

// 三平面投影：按法线权重混合三个轴向投影（贴图本身四边无缝，任何朝向都连续）
float triplanar(vec3 p, vec3 n)
{
    vec3 w = abs(n);
    w = w * w * w;                 // 更锐利的投影切换，避免斜面"三张图糊在一起"
    w /= max(w.x + w.y + w.z, 1e-5);
    float x = texture(_SkinTex, p.yz).r;
    float y = texture(_SkinTex, p.xz).r;
    float z = texture(_SkinTex, p.xy).r;
    return x * w.x + y * w.y + z * w.z;
}

vec3 sunDiffuse(vec3 normal, vec3 toSun)
{
    if (dot(toSun, toSun) < 1e-6)
        return DayColor;           // 无太阳（未喂方向）：全亮口径，与 PFLit 一致

    float incidence = dot(normal, normalize(toSun)) * 0.5 + 0.5;
    float t = clamp((incidence - 0.45) / 0.10, 0.0, 1.0);
    return mix(NightColor, DayColor, t);
}

void main()
{
    vec3 N = normalize(vNormal);
    vec3 sunDir = uMaterial._SunDir.xyz;

    // 板面：中性灰贴图 × 阵营 tint（y = 1 时保留全部板缝细节；将来可整体压平）
    float plate = triplanar(vSkinPos * uMaterial._SkinParams.x, N);
    float detail = mix(1.0, plate, clamp(uMaterial._SkinParams.y, 0.0, 1.0));
    vec3 albedo = uMaterial._Color.rgb * detail;

    // 受光：与 PFLit 同一口径
    vec3 light = sunDiffuse(N, sunDir) * uMaterial._SunColor.rgb;
    vec3 color = albedo * light;

    // 克制的轮廓 rim：把船从深空里"剥"出来，不做发光（强度默认 0.18）
    vec3 toCam = uMaterial._CamPos.xyz - vWorldPos;
    float camLen = length(toCam);
    vec3 V = (camLen > 1e-6) ? (toCam / camLen) : vec3(0.0, 0.0, 1.0);
    float rim = pow(clamp(1.0 - abs(dot(N, V)), 0.0, 1.0), 4.0);
    color += uMaterial._Color.rgb * rim * max(uMaterial._SkinParams.z, 0.0) * light;

    // 屏幕空间"几何边"亮棱（2026-10-19 游戏内实拍修正）：盒体/浮雕的面交界处法线突变，
    // fwidth 在两面之间给出 1–2 px 的尖峰 ⇒ 给现有几何描一条不随距离消失的倒角亮棱。
    // "不改几何"前提下，这是把方块读成硬表面的关键（贴图缝在 300 m 外会被 mip 抹平）。
    float edge = smoothstep(0.05, 0.6, length(fwidth(N)));
    color += uMaterial._Color.rgb * light * edge * max(uMaterial._SkinParams.w, 0.0);

    outColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
