#version 450

layout(location = 0) noperspective in vec2 vShape;
layout(location = 1) flat in float vHalfLength;
layout(location = 2) flat in vec4 vColor;
layout(location = 0) out vec4 outColor;

void main()
{
    // The same capsule becomes a round dot when its centerline shrinks to zero.
    float distanceToLine = length(vec2(vShape.x, max(abs(vShape.y) - vHalfLength, 0.0)));
    float edge = 1.0 - smoothstep(0.20, 1.0, distanceToLine);
    float along = clamp(0.5 + 0.5 * vShape.y / max(vHalfLength, 0.001), 0.0, 1.0);
    float tailFade = mix(1.0, mix(0.05, 1.0, along * along), smoothstep(0.2, 1.5, vHalfLength));
    outColor = vec4(vColor.rgb * (vColor.a * edge * tailFade), 1.0);
}
