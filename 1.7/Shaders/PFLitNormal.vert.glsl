#version 450

// PFLitNormal.vert.glsl - PFLit variant with a correct model-space normal
// transform and normal-map-ready UV/world position varyings.
//
// PFLit's original vertex shader passes inNormal through unchanged. This
// variant uses the inverse-transpose of the model matrix so that rotated and
// non-uniformly scaled part meshes receive correct lighting and tangent basis.

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)
layout(location = 3) in vec4 inUv0;      // @vertex TexCoord0 optional

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec3 vSurfacePos;


layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;

    mat3 model3 = mat3(push.modelMatrix);
    vNormal = normalize(transpose(inverse(model3)) * inNormal);
    vUv = inUv0.xy;
    vWorldPos = world.xyz;
    // Translation-free model-space position in view units: stable triplanar
    // coordinates that rotate/scale with the ship instead of sliding with it.
    vSurfacePos = world.xyz - push.modelMatrix[3].xyz;
}
