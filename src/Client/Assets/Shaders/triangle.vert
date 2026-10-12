#version 460 core

layout(location = 0) in vec3 a_Position;
layout(location = 1) in vec3 a_Color;

uniform float u_Angle;
uniform vec2 u_Offset;

out vec3 v_Color;

void main()
{
    float c = cos(u_Angle);
    float s = sin(u_Angle);
    vec2 p = mat2(c, s, -s, c) * a_Position.xy * 0.6 + u_Offset;

    gl_Position = vec4(p, a_Position.z, 1.0);
    v_Color = a_Color;
}
