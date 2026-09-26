#version 450
// the attenuation of the main light (1 lit, 0 in shadow) with a 3x3 filter; reversed Z: nearer the light is greater
layout(location = 0) in vec4 lightPosition;
layout(set = 0, binding = 0) uniform sampler2DShadow shadowMap;
layout(location = 0) out vec4 outColor;

void main() {
    vec3 p = lightPosition.xyz / lightPosition.w;
    vec2 uv = p.xy * 0.5 + 0.5;
    vec2 texel = 1.0 / vec2(textureSize(shadowMap, 0));
    float lit = 0.0;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            lit += texture(shadowMap, vec3(uv + vec2(x, y) * texel, p.z));
    lit /= 9.0;
    outColor = vec4(lit, lit, lit, 1.0);
}
