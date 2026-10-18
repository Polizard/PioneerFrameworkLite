#version 450

// 装饰实体使用方向光，保留主体 PF_RingLit 的原版昼夜外观。
// PF_RingLit.vert 传入经模型旋转后的世界法线，与太阳方向处于同一坐标系。
layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec4 vColor;
layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _OrnamentLight; // @default = vec4(0.18, 0.82, 0.18, 32.0) ambient/diffuse/specular/power
    vec4 _RimColor;      // @color @default = vec4(0.45, 0.62, 0.95, 1.0)
    vec4 _RingSunDir;    // @default = vec4(0.3, 0.75, -0.55, 0.0) light propagation direction
    vec4 _RingCameraPos; // @default = vec4(0.0, 0.0, 0.0, 1.0)
    vec4 _RingTime;      // @default = vec4(0.0, 0.0, 0.0, 0.0)
} uMaterial;
layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec3 safeUnit(vec3 value, vec3 fallback)
{
    float squaredLength = dot(value, value);
    return squaredLength > 1e-12 ? value * inversesqrt(squaredLength) : fallback;
}

void main()
{
    vec3 N = safeUnit(vNormal, vec3(0.0, 1.0, 0.0));
    vec3 L = -safeUnit(uMaterial._RingSunDir.xyz, normalize(vec3(0.3, 0.75, -0.55)));
    vec3 V = safeUnit(uMaterial._RingCameraPos.xyz - vWorldPos, N);
    float nDotL = max(dot(N, L), 0.0);
    vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
    vec3 albedo = texture(_MainTex, uv).rgb * uMaterial._Color.rgb * vColor.rgb;

    float diffuse = max(uMaterial._OrnamentLight.x, 0.0)
                  + max(uMaterial._OrnamentLight.y, 0.0) * nDotL;
    vec3 ambientTint = mix(vec3(0.65, 0.76, 0.90), vec3(1.0), nDotL);
    vec3 color = albedo * ambientTint * diffuse;
    // L + V 为零时也保持有限值；背光面不产生太阳高光。
    vec3 H = safeUnit(L + V, N);
    float specular = pow(max(dot(N, H), 0.0), max(uMaterial._OrnamentLight.w, 1.0))
                  * max(uMaterial._OrnamentLight.z, 0.0) * nDotL;
    color += vec3(specular);
    float rim = pow(clamp(1.0 - abs(dot(N, V)), 0.0, 1.0), 3.0) * 0.06;
    color += albedo * uMaterial._RimColor.rgb * rim;
    outColor = vec4(color, 1.0);
}
