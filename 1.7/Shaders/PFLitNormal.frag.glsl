#version 450

// PFLitNormal.frag.glsl - PFLit lighting plus a tangent-space normal map.
//
// The Soyuz asset uses triplanar projection for both albedo and normal detail.
// This removes UV seams across the prism facets and component joints while
// preserving the panel/rivet detail authored in the same textures.
//
// _NormalParams.x = normal strength. _NormalParams.y = triplanar scale in
// inverse view units (0 disables triplanar and falls back to the UV derivative
// TBN path). A normal strength <= 0 disables normal sampling entirely.

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec3 vSurfacePos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _SunDir;     // @default = vec4(1.0, 0.0, 0.0, 0.0) xyz = direction toward the sun (view space)
    vec4 _SunColor;   // @default = vec4(1.0, 0.98, 0.9, 1.0)
    vec4 _Emission;   // @default = vec4(0.0, 0.0, 0.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _NormalParams; // @default = vec4(0.0, 0.0, 0.0, 0.0) x = normal strength, y = triplanar scale (0 = UV path)
    vec4 _CamPos;     // @default = vec4(0.0, 0.0, 0.0, 1.0) xyz = view-space camera position
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
    vec3 baseNormal = normalize(vNormal);
    vec3 n = baseNormal;

    float normalStrength = max(uMaterial._NormalParams.x, 0.0);
    float triplanarScale = max(uMaterial._NormalParams.y, 0.0);
    float triplanarMode = step(1e-5, triplanarScale);

    vec3 p = vSurfacePos * triplanarScale;
    vec3 weights = pow(abs(baseNormal), vec3(4.0));
    weights /= max(weights.x + weights.y + weights.z, 1e-5);

    // Triplanar albedo: no UV seams across the prism facets or the component
    // joints. The UV path remains as a fallback for future PFLitNormal users.
    vec3 texColor;
    vec4 tex;
    if (triplanarMode > 0.5)
    {
        vec3 cx = texture(_MainTex, p.zy).rgb;
        vec3 cy = texture(_MainTex, p.xz).rgb;
        vec3 cz = texture(_MainTex, p.xy).rgb;
        texColor = cx * weights.x + cy * weights.y + cz * weights.z;
        tex = vec4(texColor, 1.0);
    }
    else
    {
        tex = texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
        texColor = tex.rgb;
    }

    if (normalStrength > 1e-5)
    {
        if (triplanarMode > 0.5)
        {
            vec3 nx = texture(_NormalTex, p.zy).xyz * 2.0 - 1.0;
            vec3 ny = texture(_NormalTex, p.xz).xyz * 2.0 - 1.0;
            vec3 nz = texture(_NormalTex, p.xy).xyz * 2.0 - 1.0;
            nx.xy *= normalStrength;
            ny.xy *= normalStrength;
            nz.xy *= normalStrength;

            // X projection: U=Z, V=Y, base=X; Y: U=X, V=Z, base=Y;
            // Z: U=X, V=Y, base=Z. Blend the projected normals in view space.
            vec3 wX = vec3(nx.z, nx.y, nx.x);
            vec3 wY = vec3(ny.x, ny.z, ny.y);
            vec3 wZ = vec3(nz.x, nz.y, nz.z);
            n = normalize(wX * weights.x + wY * weights.y + wZ * weights.z);
        }
        else
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
                vec3 bitangent = normalize(cross(baseNormal, tangent));
                vec3 sampled = texture(_NormalTex, vUv).xyz * 2.0 - 1.0;
                sampled.xy *= normalStrength;
                n = normalize(mat3(tangent, bitangent, baseNormal) * normalize(sampled));
            }
        }
    }

    vec3 dayNight = sunDiffuseLight(n, uMaterial._SunDir.xyz) * uMaterial._SunColor.rgb;
    vec3 color = uMaterial._Color.rgb * texColor * dayNight + uMaterial._Emission.rgb * texColor;

    // Restrained hard-surface response: the normal map should be visible on
    // large panels without turning the test asset into a glossy plastic toy.
    vec3 toCam = uMaterial._CamPos.xyz - vWorldPos;
    float camLen = length(toCam);
    vec3 viewDir = camLen > 1e-6 ? toCam / camLen : vec3(0.0, 0.0, 1.0);
    vec3 sunDir = normalize(uMaterial._SunDir.xyz + vec3(1e-6, 0.0, 0.0));
    vec3 halfDir = normalize(sunDir + viewDir);
    float specular = pow(max(dot(n, halfDir), 0.0), 28.0) * 0.12;
    float rim = pow(clamp(1.0 - abs(dot(n, viewDir)), 0.0, 1.0), 4.0) * 0.08;
    color += (vec3(specular) + vec3(0.45, 0.64, 1.0) * rim) * dayNight;

    outColor = vec4(color, uMaterial._Color.a * tex.a);
}
