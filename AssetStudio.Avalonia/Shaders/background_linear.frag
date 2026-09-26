#version 450
layout(location = 0) in float t;
layout(location = 0) out vec4 outColor;

// background.frag for an sRGB target (the game shaders of linear projects): the same gradient once encoded
vec3 toLinear(vec3 c) {
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void main() {
    vec3 top = vec3(70.0, 70.0, 80.0) / 255.0;
    vec3 bottom = vec3(40.0, 40.0, 50.0) / 255.0;
    outColor = vec4(toLinear(mix(top, bottom, clamp(t, 0.0, 1.0))), 1.0);
}
