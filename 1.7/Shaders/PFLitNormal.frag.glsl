#version 450

// PFLitNormal.frag.glsl - PFLit lighting plus a tangent-space normal map.
//
// The normal map is sampled through the mesh UV and decoded to a tangent-space
// normal. Because the shared WorldQuadBatch path preserves UVs but not tangent
// attributes, the tangent basis is rebuilt from screen-space derivatives of
// world position and UV. This keeps the existing batching path usable while
// still allowing the Soyuz prism asset to carry fine panel/rivet detail.
//
// _NormalParams.x = normal strength. A value <= 0 disables normal sampling, so
// PFLitNormal can safely fall back to the same flat look as PFLit.

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _SunDir;     // @default = vec4(1.0, 0.0, 0.0, 0.0) xyz = direction toward the sun (view space)
    vec4 _SunColor;   // @default = vec4(1.0, 0.98, 0.9, 1.0)
    vec4 _Emission;   // @default = vec4(0.0, 0.0, 0.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _NormalParams; // @default = vec4(0.0, 0.0, 0.0, 0.0) x = normal strength
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _NormalTex;

const vec3 NightColor = vec3(0.482, 0.603, 0.682);
const vec3 DayColor = vec3(1.0, 1.0, 1.0);

vec3 sunDiffuseLight(vec3 normal, vec3 toSun)
{
    if (dot(toSun, toSun) < 1e-6)
        return DayColor;

    float incidence = dot(normal, normalize(toSun)) * 0.5 + 0.5;
    float t = clamp((incidence - 0.45) / 0.10, 0.0, 1.0);
    return mix(NightColor, DayColor, t);
}

void main()
{
    vec3 n = normalize(vNormal);

    float normalStrength = max(uMaterial._NormalParams.x, 0.0);
    if (normalStrength > 1e-5)
    {
        vec3 dpdx = dFdx(vWorldPos);
        vec3 dpdy = dFdy(vWorldPos);
        vec2 duvdx = dFdx(vUv);
        vec2 duvdy = dFdy(vUv);

        vec3 tangent = dpdx * duvdy.y - dpdy * duvdx.y;
        float tangentLen = length(tangent);
        if (tangentLen > 1e-6)
        {
            tangent /= tangentLen;
            vec3 bitangent = normalize(cross(n, tangent));
            vec3 sampled = texture(_NormalTex, vUv).xyz * 2.0 - 1.0;
            sampled.xy *= normalStrength;
            n = normalize(mat3(tangent, bitangent, n) * normalize(sampled));
        }
    }

    vec3 dayNight = sunDiffuseLight(n, uMaterial._SunDir.xyz) * uMaterial._SunColor.rgb;
    vec4 tex = texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
    vec3 color = uMaterial._Color.rgb * tex.rgb * dayNight + uMaterial._Emission.rgb * tex.rgb;
    outColor = vec4(color, uMaterial._Color.a * tex.a);
}
