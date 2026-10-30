#version 450

// 2026-10-20：只输出视图系位置；舰体局部坐标由片元阶段的显式原点/逆姿态恢复。
// 零件合批以单位模型矩阵提交，地图浮雕保留 TRS；两条路径共享相同蒙皮口径。
layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;
layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec3 vWorldPos;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;
    vNormal = mat3(push.modelMatrix) * inNormal;
    vWorldPos = world.xyz;
}
