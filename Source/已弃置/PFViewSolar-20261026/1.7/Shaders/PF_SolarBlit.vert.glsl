#version 450

// Native material Blit supplies an XZ quad and an orthographic ViewProj.
layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 required
layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } uVertex;
layout(location = 0) out vec2 vUv;

void main()
{
    gl_Position = uVertex.ViewProj * vec4(inPosition, 1.0);
    vUv = inUv0.xy;
}
