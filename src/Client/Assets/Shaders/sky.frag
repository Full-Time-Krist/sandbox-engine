#version 460 core

in vec2 v_Uv;

uniform sampler2DArray u_Textures;
uniform float u_Layer;

out vec4 o_Color;

void main()
{
    vec4 texel = texture(u_Textures, vec3(v_Uv, u_Layer));
    // Прозрачные углы картинки не рисуем: светило кажется круглее.
    if (texel.a < 0.5)
        discard;
    o_Color = texel;
}
