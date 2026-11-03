#version 450

// 云图采样/噪声扭曲/偏移自阴影沿用原版 World/Clouds 的表现方式。
// PFView 显式提供太阳、相对球心相机位置及模拟时钟相位，不读世界地图全局状态。
// 只给云覆盖率写 alpha，保留原有蓝色大气边缘，避免额外铺满一层蓝雾。
layout(location = 0) in vec3 vLocalDirection;
layout(location = 1) in vec3 vViewOffset;
layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _CloudToSun;
    vec4 _CloudSunLocal;
    vec4 _CloudCameraFromBody;
    vec4 _CloudParams; // x=opacity, y=drift phase, z=noise phase, w=coverage multiplier
    vec4 _CloudTint; // v1/legacy=(1,1,1,1); explicit v2=resolved profile RGBA
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _NoiseTex;
layout(set = 2, binding = 2) uniform sampler2D _SunsetColorRamp;
layout(set = 2, binding = 3) uniform sampler2D _CloudTex;

float cloudCover(vec3 direction)
{
    // 与原版一致：经度 + 方向的 Y 分量；UV 随地球固连系旋转。
    float longitude = atan(direction.z, direction.x) / 6.28318530718;
    vec2 uv = vec2(0.5 + longitude, 0.5 + 0.5 * direction.y);
    uv.x += uMaterial._CloudParams.y + 0.173;
    vec2 noiseUv = uv * vec2(20.0, 10.0);
    noiseUv.y += uMaterial._CloudParams.z;
    vec2 distortion = 1.0 - 2.0 * texture(_NoiseTex, vec2(noiseUv.x, -noiseUv.y)).xy;
    uv += distortion * 0.0015;
    float cover = textureLod(_CloudTex, vec2(1.0 - uv.x, 1.0 - uv.y), 0.0).r;
    return smoothstep(0.2, 0.5, sqrt(max(cover, 0.0))) * cover * uMaterial._CloudParams.w;
}

void main()
{
    vec3 normal = normalize(vViewOffset);
    vec3 viewDir = normalize(uMaterial._CloudCameraFromBody.xyz - vViewOffset);
    float facing = dot(normal, viewDir);
    float cover = cloudCover(normalize(vLocalDirection));
    float alpha = cover * uMaterial._CloudParams.x * uMaterial._CloudTint.a * smoothstep(0.0, 0.04, facing);
    if (alpha < 0.001) discard;

    float sunlight = dot(normal, normalize(uMaterial._CloudToSun.xyz));
    float day = smoothstep(-0.2, 0.2, sunlight);
    float twilight = 1.0 - smoothstep(0.0, 0.2, abs(sunlight));
    float sunsetUv = 1.0 - smoothstep(-0.2, 0.2, sunlight);
    vec3 sunset = textureLod(_SunsetColorRamp, vec2(sunsetUv, 0.5), 0.0).rgb;

    // 沿太阳方向再采样一次，模拟云带内部的遮蔽；全程在固连系取样。
    vec3 shadowDir = normalize(normalize(vLocalDirection) + normalize(uMaterial._CloudSunLocal.xyz) * 0.01);
    float shadow = clamp(cloudCover(shadowDir) - cover, 0.0, 1.0);
    vec3 dayColor = mix(vec3(0.9, 0.9, 0.95), sunset, twilight * 0.65);
    vec3 color = mix(vec3(0.25, 0.25, 0.35), dayColor, day) * (1.0 - shadow * 0.6);
    outColor = vec4(color * uMaterial._CloudTint.rgb, alpha);
}
