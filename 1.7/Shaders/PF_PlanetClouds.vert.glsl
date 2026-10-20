#version 450

// PFView 云层：保留固连系采样方向，并输出视图系法线与相对球心位置。
// 浮动原点/远景挪位均由 modelMatrix 承担；片元阶段不假设球心在世界原点。
layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(push_constant) uniform Push { mat4 modelMatrix; } push;
layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(location = 0) out vec3 vLocalDirection;
layout(location = 1) out vec3 vViewOffset;

void main()
{
    gl_Position = uVertex.ViewProj * (push.modelMatrix * vec4(inPosition, 1.0));
    vLocalDirection = inPosition;
    vViewOffset = mat3(push.modelMatrix) * inPosition;
}
