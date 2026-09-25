#version 450
layout(push_constant) uniform Push {
    mat4 mvp;
    mat3 view;
    vec4 color;
} pc;

layout(location = 0) in vec3 viewPos;
layout(location = 1) in vec3 viewNormal;
layout(location = 0) out vec4 outColor;

void main() {
    vec3 n;
    if (pc.color.a > 0.5 && dot(viewNormal, viewNormal) > 1e-12)
        n = normalize(viewNormal);
    else
        n = normalize(cross(dFdx(viewPos), dFdy(viewPos))); // flat shading when the mesh has no normals
    // two sided: flip normals facing away from the viewer (+Z points to the viewer)
    if (n.z < 0.0)
        n = -n;
    vec3 key = normalize(vec3(0.3, 0.5, 1.0));
    vec3 fill = normalize(vec3(-0.6, -0.2, 0.5));
    float diffuse = max(dot(n, key), 0.0) * 0.75 + max(dot(n, fill), 0.0) * 0.2;
    vec3 h = normalize(key + vec3(0.0, 0.0, 1.0));
    float spec = pow(max(dot(n, h), 0.0), 48.0) * 0.25;
    vec3 albedo = vec3(0.86, 0.88, 0.92);
    vec3 c = albedo * (0.22 + diffuse) + vec3(spec);
    outColor = vec4(clamp(c, 0.0, 1.0), 1.0);
}
