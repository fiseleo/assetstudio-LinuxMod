#version 450
layout(push_constant) uniform Push {
    mat4 mvp;
    mat3 view;
    vec4 color;
} pc;

layout(location = 0) out vec4 outColor;

void main() {
    outColor = vec4(pc.color.rgb, 1.0);
}
