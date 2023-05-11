#version 450

layout(location = 0) in vec3 iNormal;
layout(location = 1) in vec2 iUV;

layout(location = 0) out vec4 oColor;

void main() {
    // no sampler
    oColor = vec4(iUV, 0.0, 1.0);
}