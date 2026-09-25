#version 450
// Fullscreen triangle, no vertex buffer.
layout(location = 0) out float t;

void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 1.0, 1.0);
    t = p.y;
}
