#version 450

// 2026-10-20：工业装甲蒙皮。保留原几何/提交数，使用 image2 板面与局部蓝色涂装遮罩。
// 灰度只提供板面明度与浅凹槽；蓝色区域由 _Color 换成阵营色，主体保持中性金属。
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec3 vWorldPos;
layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;        // @color @default = vec4(0.76, 0.80, 0.86, 1.0)
    vec4 _SunDir;       // @default = vec4(1.0, 0.0, 0.0, 0.0)
    vec4 _SunColor;     // @default = vec4(1.0, 0.98, 0.9, 1.0)
    vec4 _CamPos;       // @default = vec4(0.0, 0.0, 0.0, 1.0)
    vec4 _SkinParams;   // @default = vec4(0.0625, 1.0, 0.08, 0.025) x=每米重复数 y=细节 z=rim w=凹槽高度(米)
    vec4 _SkinOrigin;   // @default = vec4(0.0, 0.0, 0.0, 10.0) xyz=舰体视图系原点 w=视图单位转物理米
    vec4 _SkinRotation; // @default = vec4(0.0, 0.0, 0.0, 1.0) 视图系转舰体系的单位四元数
    vec4 _HullColor;    // @color @default = vec4(0.74, 0.77, 0.80, 1.0)
    vec4 _SkinSurface;  // @default = vec4(0.0, 0.76, 0.0, 0.0) x=零件合批(法线仍在舰体系) y=粗糙度
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _SkinTex;
const vec3 NightColor = vec3(0.482, 0.603, 0.682);
const vec3 DayColor = vec3(1.0, 1.0, 1.0);

vec3 rotateVector(vec4 q, vec3 v)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

// 纹理使用引擎 MirroredRepeat；保留未折叠 UV，硬件正确处理镜像边界的梯度和过滤核。
vec3 triplanar(vec3 p, vec3 n)
{
    vec3 w = abs(n);
    w = w * w * w;
    w /= max(w.x + w.y + w.z, 1e-5);
    return texture(_SkinTex, p.yz).rgb * w.x
        + texture(_SkinTex, p.xz).rgb * w.y
        + texture(_SkinTex, p.xy).rgb * w.z;
}

vec3 sunDiffuse(vec3 normal, vec3 toSun)
{
    if (dot(toSun, toSun) < 1e-6) return DayColor;
    float incidence = dot(normal, normalize(toSun)) * 0.5 + 0.5;
    float t = clamp((incidence - 0.45) / 0.10, 0.0, 1.0);
    return mix(NightColor, DayColor, t);
}

void main()
{
    vec4 inverseRotation = uMaterial._SkinRotation;
    vec4 forwardRotation = vec4(-inverseRotation.xyz, inverseRotation.w);
    vec3 N = normalize(vNormal);
    // WorldQuadBatch 只变换了位置；轴对齐盒体的局部面法线在此恢复到同一盏太阳的视图系。
    if (uMaterial._SkinSurface.x > 0.5)
        N = rotateVector(forwardRotation, N);

    vec3 shipMeters = rotateVector(inverseRotation, vWorldPos - uMaterial._SkinOrigin.xyz)
        * uMaterial._SkinOrigin.w;
    vec3 shipNormal = rotateVector(inverseRotation, N);
    vec3 tex = triplanar(shipMeters * uMaterial._SkinParams.x, shipNormal);
    float luminance = dot(tex, vec3(0.299, 0.587, 0.114));
    float paint = smoothstep(0.045, 0.14, tex.b - tex.r);

    // 距离由物理米/像素决定，兼容跟随镜头、参考系切换和调试放大；远处减弱细节。
    float footprint = length(fwidth(shipMeters));
    float fineDetail = 1.0 - smoothstep(0.15, 1.25, footprint);
    float detail = clamp(uMaterial._SkinParams.y, 0.0, 1.0);
    float plate = mix(0.90, clamp(luminance * 1.55, 0.30, 1.10), detail * mix(0.4, 1.0, fineDetail));
    vec3 base = uMaterial._HullColor.rgb * plate;
    vec3 coating = uMaterial._Color.rgb * mix(0.82, 1.0, clamp(plate, 0.0, 1.0));
    vec3 albedo = mix(base, coating, paint);

    // 从凹槽高度的屏幕导数恢复表面梯度，仅扰动着色法线；顶点和剪影保持原样。
    vec3 dpdx = dFdx(vWorldPos) * uMaterial._SkinOrigin.w;
    vec3 dpdy = dFdy(vWorldPos) * uMaterial._SkinOrigin.w;
    float height = smoothstep(0.25, 0.58, luminance) * max(uMaterial._SkinParams.w, 0.0);
    vec3 r1 = cross(dpdy, N);
    vec3 r2 = cross(N, dpdx);
    float determinant = dot(dpdx, r1);
    vec3 gradient = (r1 * dFdx(height) + r2 * dFdy(height))
        * sign(determinant) / max(abs(determinant), 1e-7);
    vec3 shadedNormal = normalize(N - gradient * fineDetail * detail);

    vec3 toCam = uMaterial._CamPos.xyz - vWorldPos;
    vec3 V = toCam / max(length(toCam), 1e-6);
    vec3 light = sunDiffuse(shadedNormal, uMaterial._SunDir.xyz) * uMaterial._SunColor.rgb;
    vec3 color = albedo * light;
    float rim = pow(clamp(1.0 - abs(dot(N, V)), 0.0, 1.0), 4.0);
    color += uMaterial._HullColor.rgb * rim * max(uMaterial._SkinParams.z, 0.0) * light;

    // 单盏太阳的宽弱高光；无需环境贴图或额外渲染通道。背光面不产生镜面亮斑。
    vec3 sun = uMaterial._SunDir.xyz;
    if (dot(sun, sun) > 1e-6)
    {
        vec3 L = normalize(sun);
        vec3 halfVector = L + V;
        vec3 H = halfVector / max(length(halfVector), 1e-6);
        float roughness = clamp(uMaterial._SkinSurface.y - paint * 0.10 + (plate - 0.90) * 0.12, 0.45, 0.95);
        float specular = pow(max(dot(shadedNormal, H), 0.0), mix(52.0, 10.0, roughness))
            * max(dot(shadedNormal, L), 0.0) * 0.10;
        color += uMaterial._SunColor.rgb * specular;
    }

    outColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
