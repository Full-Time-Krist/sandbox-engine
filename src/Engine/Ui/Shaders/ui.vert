#version 460 core

layout(location = 0) in vec2 a_Position;
layout(location = 1) in vec2 a_Uv;
layout(location = 2) in float a_Layer;
layout(location = 3) in vec4 a_Color;

// Размер экрана в пикселях: вершины интерфейса заданы в пикселях от левого верхнего угла.
uniform vec2 u_ScreenSize;

out vec2 v_Uv;
flat out float v_Layer;
out vec4 v_Color;

void main()
{
    // Пиксели → NDC: X от −1 слева до 1 справа, Y от 1 сверху до −1 снизу (на экране Y растёт вниз).
    vec2 ndc = a_Position / u_ScreenSize * 2.0 - 1.0;
    gl_Position = vec4(ndc.x, -ndc.y, 0.0, 1.0);
    v_Uv = a_Uv;
    v_Layer = a_Layer;
    v_Color = a_Color;
}
