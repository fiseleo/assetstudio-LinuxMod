#version 450
// Unity's screen space shadows: the model from the camera (Unity's clip space of the game shaders), with its position
// in the shadow map
layout(location = 0) in vec3 position;
layout(push_constant) uniform Push { mat4 viewProjection; mat4 lightViewProjection; } push;
layout(location = 0) out vec4 lightPosition;

void main() {
    // the camera unflipped (not like a render texture's): the rows as ComputeScreenPos reads them
    gl_Position = push.viewProjection * vec4(position, 1.0);
    lightPosition = push.lightViewProjection * vec4(position, 1.0);
}
