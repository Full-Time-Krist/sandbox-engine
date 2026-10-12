#version 460 core

layout(location = 0) in vec3 a_Position;
layout(location = 1) in vec3 a_Color;

uniform float u_Time;

out vec3 v_Color;

void main()
{
    // Медленное вращение вокруг оси Z: так видно, что uniform доходит до шейдера.
    float c = cos(u_Time);
    float s = sin(u_Time);
    vec2 p = mat2(c, s, -s, c) * a_Position.xy;

    gl_Position = vec4(p, a_Position.z, 1.0);
    v_Color = a_Color;
}
