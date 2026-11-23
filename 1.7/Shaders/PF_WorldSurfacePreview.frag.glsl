#version 450

layout(location = 0) in vec3 vNormal;
layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _SunDir;     // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _Patch;      // @default = vec4(0.0, 1.0, 0.0, 0.0)
    vec4 _PatchRight; // @default = vec4(1.0, 0.0, 0.0, 0.0)
    vec4 _PatchUp;    // @default = vec4(0.0, 0.0, 1.0, 0.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    vec3 direction = normalize(vNormal);
    vec3 color = uMaterial._Color.rgb;
    if (uMaterial._Patch.w > 0.0)
    {
        vec2 tangent = vec2(dot(direction, uMaterial._PatchRight.xyz), dot(direction, uMaterial._PatchUp.xyz));
        float tangentLength = length(tangent);
        float angle = atan(tangentLength, dot(direction, uMaterial._Patch.xyz));
        // Same azimuthal equidistant projection as the bake, including the full-world antipode.
        vec2 delta = tangentLength > 1e-6 ? tangent / tangentLength : vec2(1.0, 0.0);
        vec2 uv = 0.5 + delta * (0.5 * angle / uMaterial._Patch.w);
        vec4 patch = texture(_MainTex, uv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
        if (angle <= uMaterial._Patch.w)
            color = mix(color, color * (1.0 - patch.a) + patch.rgb, uMaterial._PatchRight.w);
    }

    vec3 sun = uMaterial._SunDir.xyz;
    float daylight = dot(sun, sun) > 1e-6 ? clamp(0.5 + 5.0 * dot(direction, normalize(sun)), 0.0, 1.0) : 1.0;
    outColor = vec4(color * mix(vec3(0.482, 0.603, 0.682), vec3(1.0), daylight), uMaterial._Color.a);
}
