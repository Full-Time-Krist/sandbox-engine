#version 460 core

// Трещины поверх блока, который ломают: куб чуть больше блока, текстура — стадия трещины из массива текстур.
layout(location = 0) in vec3 a_Position;
layout(location = 1) in vec2 a_Uv;

uniform mat4 u_ViewProjection;
uniform mat4 u_Model;

out vec2 v_Uv;

void main()
{
    gl_Position = u_ViewProjection * u_Model * vec4(a_Position, 1.0);
    v_Uv = a_Uv;
}
