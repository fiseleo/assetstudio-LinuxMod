#version 450
// Unity's screen space shadows: the model from the camera (Unity's clip space of the game shaders), with its position
// in the shadow map
layout(location = 0) in vec3 position;
layout(push_constant) uniform Push { mat4 viewProjection; mat4 lightViewProjection; } push;
layout(location = 0) out vec4 lightPosition;

void main() {
    gl_Position = push.viewProjection * vec4(position, 1.0);
    // the game shaders' camera is flipped like a render texture's (_ProjectionParams.x = -1): their ComputeScreenPos
    // reads the rows of the screen space shadows the other way
    gl_Position.y = -gl_Position.y;
    lightPosition = push.lightViewProjection * vec4(position, 1.0);
}
