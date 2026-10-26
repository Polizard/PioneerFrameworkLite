#version 450

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms { vec4 _SolarBlur; } uMaterial;
layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec3 tap(vec2 uv)
{
    vec3 c = texture(_MainTex, uv).rgb;
    float peak = max(c.r, max(c.g, c.b));
    return mix(c, c * max(peak - 1.25, 0.0) / max(peak, 0.0001), uMaterial._SolarBlur.z);
}

void main()
{
    vec2 stepUv = uMaterial._SolarBlur.xy;
    vec3 c = tap(vUv) * 0.227027;
    c += (tap(vUv + stepUv * 1.384615) + tap(vUv - stepUv * 1.384615)) * 0.316216;
    c += (tap(vUv + stepUv * 3.230769) + tap(vUv - stepUv * 3.230769)) * 0.070270;
    outColor = vec4(c, 0.0);
}
