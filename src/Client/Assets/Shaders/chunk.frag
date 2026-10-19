#version 460 core

in vec2 v_Uv;
flat in float v_Layer;
in float v_Shade;
in float v_Sky;
in float v_BlockLight;
in float v_Distance;

uniform sampler2DArray u_Textures;
// Яркость неба по времени суток (DayCycle.SkyBrightness): 1 днём, 4/15 ночью. Умножает уровень небесного света.
uniform float u_SkyBrightness;

// Туман — цвет неба по краю прорисовки (Atmosphere): прячет обрыв мира, вдали видны ночь и закат.
uniform vec3 u_FogColor;
uniform float u_FogStart;
uniform float u_FogEnd;

out vec4 o_Color;

// Кривая яркости — та же, что в item.frag. Уровень 15 — полная яркость, каждый уровень ниже — в 0,8 раза темнее,
// а уровень 0 не чёрный: совсем тёмные пещеры неиграбельны.
const float MinBrightness = 0.05;
const vec3 BlockTint = vec3(1.0, 0.86, 0.68); // свет факелов чуть тёплый: отличается от луны

float Brightness(float level)
{
    return mix(MinBrightness, 1.0, pow(0.8, 15.0 - clamp(level, 0.0, 15.0)));
}

void main()
{
    vec3 albedo = texture(u_Textures, vec3(v_Uv, v_Layer)).rgb;
    vec3 light = max(vec3(Brightness(v_Sky * u_SkyBrightness)), Brightness(v_BlockLight) * BlockTint);
    vec3 color = albedo * light * v_Shade;
    o_Color = vec4(mix(color, u_FogColor, smoothstep(u_FogStart, u_FogEnd, v_Distance)), 1.0);
}
