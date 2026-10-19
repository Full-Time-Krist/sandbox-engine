#version 460 core

// Солнце или луна: квадрат [−1; 1]² на небесной сфере (SkyRenderer). Камера в начале координат.
layout(location = 0) in vec2 a_Corner;

uniform mat4 u_ViewProjection;
uniform vec3 u_Center; // центр квадрата: направление на светило × расстояние
uniform vec3 u_Right;  // полуоси квадрата
uniform vec3 u_Up;

out vec2 v_Uv;

void main()
{
    gl_Position = u_ViewProjection * vec4(u_Center + u_Right * a_Corner.x + u_Up * a_Corner.y, 1.0);
    // v = 0 — верх картинки.
    v_Uv = vec2(a_Corner.x * 0.5 + 0.5, 0.5 - a_Corner.y * 0.5);
}
