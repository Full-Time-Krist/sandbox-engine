#version 460 core

in vec2 v_Uv;

uniform sampler2DArray u_Textures;
uniform float u_Layer;

out vec4 o_Color;

void main()
{
    vec4 crack = texture(u_Textures, vec3(v_Uv, u_Layer));
    // Прозрачное между трещинами не рисуем: иначе куб закрыл бы блок полупрозрачной пеленой.
    if (crack.a < 0.05)
        discard;
    o_Color = crack;
}
