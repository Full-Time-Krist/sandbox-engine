#version 460 core

layout(location = 0) in vec3 a_Position;
layout(location = 1) in vec2 a_Uv;
layout(location = 2) in float a_Layer;
layout(location = 3) in float a_Shade;
layout(location = 4) in float a_Sky;
layout(location = 5) in float a_BlockLight;

uniform mat4 u_ViewProjection;
uniform mat4 u_Model;

out vec2 v_Uv;
// flat: слой одинаков у всех вершин грани, интерполировать его незачем (и опасно из-за погрешности).
flat out float v_Layer;
out float v_Shade;
// Уровни света (0–15) интерполируются по грани, кривая яркости — во фрагментном шейдере: так переход плавный.
out float v_Sky;
out float v_BlockLight;

void main()
{
    gl_Position = u_ViewProjection * u_Model * vec4(a_Position, 1.0);
    v_Uv = a_Uv;
    v_Layer = a_Layer;
    v_Shade = a_Shade;
    v_Sky = a_Sky;
    v_BlockLight = a_BlockLight;
}
