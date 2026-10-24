#version 460 core
in vec2 v_Uv;
flat in float v_Layer;
in float v_Shade;
uniform sampler2DArray u_Textures;
out vec4 o_Color;
void main() {
    vec4 color = texture(u_Textures,vec3(v_Uv,v_Layer));
    if (color.a < .5) discard;
    o_Color = vec4(color.rgb * v_Shade,1);
}
