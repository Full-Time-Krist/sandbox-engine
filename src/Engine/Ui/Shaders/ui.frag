#version 460 core

in vec2 v_Uv;
flat in float v_Layer;
in vec4 v_Color;

// Атлас интерфейса: глифы (белые, покрытие в альфе) и белый квадрат для заливки.
uniform sampler2D u_Atlas;
// Массив текстур блоков: иконки предметов рисуются гранями блоков.
uniform sampler2DArray u_Textures;

out vec4 o_Color;

void main()
{
    // Отрицательный слой — атлас, иначе слой массива. Нулевой уровень mip-карты: иконка рисуется пиксель в пиксель
    // в целом масштабе, а сжатая по вертикали верхняя грань без этого брала бы размытый уровень.
    vec4 texel = v_Layer < 0.0
        ? texture(u_Atlas, v_Uv)
        : textureLod(u_Textures, vec3(v_Uv, v_Layer), 0.0);
    o_Color = texel * v_Color;
}
