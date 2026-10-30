#version 450

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms { vec4 _SolarComposite; } uMaterial;
layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _BloomTex;

void main()
{
    vec4 scene = texture(_MainTex, vUv);
    vec3 radiance = scene.rgb + texture(_BloomTex, vUv).rgb * uMaterial._SolarComposite.x;
    vec3 color = pow(1.0 - exp(-max(radiance, vec3(0.0)) * 0.76), vec3(0.4545));
    // Premultiplied emission: tenuous plasma adds light without a black sprite rectangle.
    outColor = vec4(color, clamp(scene.a, 0.0, 1.0));
}
