#version 460 core

in vec2 v_Uv;
flat in float v_Layer;
in float v_Shade;
flat in vec2 v_Light;

uniform sampler2DArray u_Textures;
uniform float u_SkyBrightness;

out vec4 o_Color;

// Кривая яркости — та же, что в chunk.frag: предмет освещён так же, как блоки вокруг.
const float MinBrightness = 0.05;
const vec3 BlockTint = vec3(1.0, 0.86, 0.68);

float Brightness(float level)
{
    return mix(MinBrightness, 1.0, pow(0.8, 15.0 - clamp(level, 0.0, 15.0)));
}

void main()
{
    vec4 texel = texture(u_Textures, vec3(v_Uv, v_Layer));
    // У картинок предметов фон прозрачный: его не рисуем, иначе вокруг предмета был бы квадрат.
    if (texel.a < 0.5)
        discard;
    vec3 light = max(vec3(Brightness(v_Light.x * u_SkyBrightness)), Brightness(v_Light.y) * BlockTint);
    o_Color = vec4(texel.rgb * light * v_Shade, 1.0);
}
