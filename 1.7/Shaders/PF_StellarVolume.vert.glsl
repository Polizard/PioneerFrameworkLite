#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } uVertex;
layout(push_constant) uniform Push { mat4 modelMatrix; } push;
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _StellarEye;
    vec4 _StellarTime;
    vec4 _StellarParams;
    vec4 _StellarDrift;
    vec4 _StellarArchHeight0;
    vec4 _StellarArchHeight1;
} uMaterial;
layout(location = 0) out vec3 vLocalPosition;
layout(location = 1) flat out vec4 vClip0;
layout(location = 2) flat out vec4 vClip1;
layout(location = 3) flat out vec4 vClip2;
layout(location = 4) flat out vec4 vClip3;

void main()
{
    mat4 clip = uVertex.ViewProj * push.modelMatrix;
    gl_Position = clip * vec4(inPosition, 1.0);
    vLocalPosition = inPosition * (2.0 * uMaterial._StellarParams.w);
    vClip0 = clip[0]; vClip1 = clip[1]; vClip2 = clip[2]; vClip3 = clip[3];
}
