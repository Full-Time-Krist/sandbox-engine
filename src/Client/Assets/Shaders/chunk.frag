#version 460 core

in vec2 v_Uv;
flat in float v_Layer;
in float v_Shade;

uniform sampler2DArray u_Textures;

out vec4 o_Color;

void main()
{
    vec3 albedo = texture(u_Textures, vec3(v_Uv, v_Layer)).rgb;
    o_Color = vec4(albedo * v_Shade, 1.0);
}
