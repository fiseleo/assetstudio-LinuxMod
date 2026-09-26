#version 450
// the depth of the model seen from the light (the shadow map of the game shaders' preview)
layout(location = 0) in vec3 position; // Unity space
layout(push_constant) uniform Push { mat4 lightViewProjection; } push;

void main() {
    gl_Position = push.lightViewProjection * vec4(position, 1.0);
}
