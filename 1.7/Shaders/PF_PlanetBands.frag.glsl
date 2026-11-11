#version 450

// Explicit descriptive gas appearance on the existing PFView base sphere.
layout(location = 0) in vec3 vLocalDirection;
layout(location = 1) in vec3 vViewOffset;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _SunDir;
    vec4 _SunColor;
    vec4 _BandParams; // x=latitude cycles, y=contrast
    vec4 _BandColor;
} uMaterial;

void main()
{
    vec3 local = normalize(vLocalDirection);
    float latitude = asin(clamp(local.y, -1.0, 1.0));
    float longitude = atan(local.z, local.x);
    float phase = latitude * (2.0 * uMaterial._BandParams.x)
        + 0.4 * sin(longitude * 3.0 + latitude * 7.0) * cos(latitude);
    float band = 0.5 + 0.5 * sin(phase);
    vec3 albedo = uMaterial._BandColor.rgb * (1.0 - uMaterial._BandParams.y * band);
    float day = max(dot(normalize(vViewOffset), normalize(uMaterial._SunDir.xyz)), 0.0);
    outColor = vec4(albedo * (vec3(0.05) + day * uMaterial._SunColor.rgb), 1.0);
}
