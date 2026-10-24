#version 460 core
layout(location=0) in vec3 a_Position;
layout(location=1) in vec2 a_Uv;
layout(location=2) in float a_Face;
layout(location=3) in float a_Shade;
layout(location=4) in vec4 i_PositionAngle;
layout(location=5) in vec3 i_LayersA;
layout(location=6) in vec3 i_LayersB;
layout(location=7) in float i_Icon;
uniform mat4 u_ViewProjection;
uniform vec3 u_Right;
out vec2 v_Uv;
flat out float v_Layer;
out float v_Shade;
const vec2 quad[6] = vec2[6](vec2(-.5,-.5),vec2(.5,-.5),vec2(.5,.5),vec2(-.5,-.5),vec2(.5,.5),vec2(-.5,.5));
void main() {
    vec3 p;
    if (i_Icon >= 0) {
        vec2 corner = gl_VertexID < 6 ? quad[gl_VertexID] : vec2(0);
        // Картинка обращена к камере; небольшой поворот в своей плоскости показывает вращение.
        float angle = sin(i_PositionAngle.w) * .12;
        corner = mat2(cos(angle),sin(angle),-sin(angle),cos(angle)) * corner;
        p = u_Right * corner.x * .4 + vec3(0,corner.y * .4,0);
        v_Uv = gl_VertexID < 6 ? vec2(quad[gl_VertexID].x + .5, .5 - quad[gl_VertexID].y) : vec2(0);
        v_Layer = i_Icon;
        v_Shade = 1;
    } else {
        p = (a_Position - vec3(.5)) * .25;
        float c = cos(i_PositionAngle.w), s = sin(i_PositionAngle.w);
        p.xz = mat2(c,s,-s,c) * p.xz;
        int face = int(a_Face);
        v_Layer = face < 3 ? i_LayersA[face] : i_LayersB[face-3];
        v_Uv = a_Uv;
        v_Shade = a_Shade;
    }
    gl_Position = u_ViewProjection * vec4(i_PositionAngle.xyz + p,1);
}
