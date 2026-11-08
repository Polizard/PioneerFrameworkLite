#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor;    // @vertex Color required
layout(location = 3) in vec4 inShape;    // @vertex TexCoord0 required

layout(push_constant) uniform Push { mat4 modelMatrix; } push;
layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(location = 0) noperspective out vec2 vShape;
layout(location = 1) flat out float vHalfLength;
layout(location = 2) flat out vec4 vColor;

void main()
{
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
    vShape = inShape.xy;
    vHalfLength = inShape.z;
    vColor = inColor;
}
