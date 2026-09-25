#version 450
layout(location = 0) in float t;
layout(location = 0) out vec4 outColor;

void main() {
    // same gradient as the software renderer (top rgb 70,70,80 -> bottom 40,40,50)
    vec3 top = vec3(70.0, 70.0, 80.0) / 255.0;
    vec3 bottom = vec3(40.0, 40.0, 50.0) / 255.0;
    outColor = vec4(mix(top, bottom, clamp(t, 0.0, 1.0)), 1.0);
}
