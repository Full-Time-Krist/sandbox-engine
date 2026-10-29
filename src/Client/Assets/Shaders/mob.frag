#version 460 core

in vec2 v_Uv;
flat in float v_Layer;
in float v_Shade;
flat in vec2 v_Light;
flat in float v_Flash;
in float v_Distance;

uniform sampler2DArray u_Textures; // текстуры мобов, 64×64
uniform float u_SkyBrightness;

// Туман — цвет неба по краю прорисовки (Atmosphere), как у чанков и предметов.
uniform vec3 u_FogColor;
uniform float u_FogStart;
uniform float u_FogEnd;

out vec4 o_Color;

// Кривая яркости — та же, что в chunk.frag и item.frag: моб освещён так же, как блоки вокруг.
const float MinBrightness = 0.05;
const vec3 BlockTint = vec3(1.0, 0.86, 0.68);
const vec3 FlashColor = vec3(1.0, 0.15, 0.1);

float Brightness(float level)
{
    return mix(MinBrightness, 1.0, pow(0.8, 15.0 - clamp(level, 0.0, 15.0)));
}

void main()
{
    vec4 texel = texture(u_Textures, vec3(v_Uv, v_Layer));
    // Прозрачное на развёртке (уши, просветы) не рисуем.
    if (texel.a < 0.5)
        discard;
    vec3 light = max(vec3(Brightness(v_Light.x * u_SkyBrightness)), Brightness(v_Light.y) * BlockTint);
    vec3 color = texel.rgb * light * v_Shade;
    // Вспышка видна и в темноте: красный подмешивается к освещённому цвету, но и сам светится чуть-чуть.
    color = mix(color, FlashColor * max(light, vec3(0.35)), v_Flash * 0.6);
    o_Color = vec4(mix(color, u_FogColor, smoothstep(u_FogStart, u_FogEnd, v_Distance)), 1.0);
}
