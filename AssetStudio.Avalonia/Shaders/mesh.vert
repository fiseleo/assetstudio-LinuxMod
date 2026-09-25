#version 450
// Mesh preview: positions/normals in model space, push constants from VulkanMeshRenderer.
layout(push_constant) uniform Push {
    mat4 mvp;       // model -> clip
    mat3 view;      // model -> view rotation, used for lighting (128 bytes total: the guaranteed push constant size)
    vec4 color;     // wire color (rgb), a = 1 when the mesh has normals
} pc;

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;

layout(location = 0) out vec3 viewPos;
layout(location = 1) out vec3 viewNormal;

void main() {
    gl_Position = pc.mvp * vec4(inPos, 1.0);
    viewPos = pc.view * inPos;
    viewNormal = pc.view * inNormal;
}
