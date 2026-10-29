#version 460 core

// Мобы: меши частей моделей в одном буфере, по экземпляру на часть моба (MobRenderer).
layout(location = 0) in vec3 a_Position; // от точки поворота части, в блоках
layout(location = 1) in vec2 a_Uv;
layout(location = 2) in vec3 a_Normal;

layout(location = 3) in mat4 i_Model;    // поза части со смещением от глаза (locations 3–6)
layout(location = 7) in vec2 i_Light;    // свет блока, где стоит моб: небесный и блочный
layout(location = 8) in float i_Flash;   // красная вспышка после удара, 0–1
layout(location = 9) in float i_Layer;   // слой текстуры моба

uniform mat4 u_ViewProjection;

out vec2 v_Uv;
flat out float v_Layer;
out float v_Shade;
flat out vec2 v_Light;
flat out float v_Flash;
out float v_Distance;

void main()
{
    vec4 world = i_Model * vec4(a_Position, 1.0);
    gl_Position = u_ViewProjection * world;

    // Яркость грани — как у блоков (верх 1, бока по Z 0,8, по X 0,6, низ 0,5), но по нормали после поворота:
    // повернувшийся моб не уносит освещённую сторону с собой.
    vec3 n = normalize(mat3(i_Model) * a_Normal);
    v_Shade = n.x * n.x * 0.6 + n.z * n.z * 0.8 + n.y * n.y * (n.y > 0.0 ? 1.0 : 0.5);

    v_Uv = a_Uv;
    v_Layer = i_Layer;
    v_Light = i_Light;
    v_Flash = i_Flash;
    v_Distance = length(world.xyz);
}
