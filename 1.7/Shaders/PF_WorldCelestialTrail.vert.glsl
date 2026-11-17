#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color required
layout(location = 0) out vec4 vColor;

layout(push_constant) uniform Push { mat4 modelMatrix; } push;
layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
    vColor = inColor;
}
