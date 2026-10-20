#version 460 core

// Предметы на земле: один меш (кубик + квадрат картинки), по экземпляру на предмет (ItemEntityRenderer).
layout(location = 0) in vec3 a_Position;
layout(location = 1) in vec2 a_Uv;
layout(location = 2) in float a_Face;   // 0 — верх, 1 — бок, 2 — низ
layout(location = 3) in float a_Shade;
layout(location = 4) in float a_Sprite; // вершина картинки (1) или кубика (0)

layout(location = 5) in vec3 i_Offset;  // точка предмета относительно глаза
layout(location = 6) in float i_Yaw;
layout(location = 7) in vec3 i_Layers;  // слои верха, бока, низа
layout(location = 8) in float i_Sprite;
layout(location = 9) in vec2 i_Light;   // свет блока, где лежит предмет: небесный и блочный
layout(location = 10) in vec3 i_BoxMin; // общий короб формы блока в долях блока (полублок — нижняя половина)
layout(location = 11) in vec3 i_BoxMax;

uniform mat4 u_ViewProjection;
uniform float u_CubeSize; // сторона кубика в блоках (ItemEntityRenderer.CubeSize)

out vec2 v_Uv;
flat out float v_Layer;
out float v_Shade;
flat out vec2 v_Light;
out float v_Distance;

void main()
{
    v_Uv = a_Uv;
    v_Shade = a_Shade;
    v_Light = i_Light;
    v_Layer = a_Face < 0.5 ? i_Layers.x : (a_Face < 1.5 ? i_Layers.y : i_Layers.z);

    // Не та часть меша: все её вершины в одной точке за экраном — треугольники нулевой площади отбрасываются.
    if (abs(a_Sprite - i_Sprite) > 0.5)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }

    vec3 local;
    if (i_Sprite > 0.5)
    {
        // Картинка повёрнута к камере по горизонтали (камера в начале координат) и стоит вертикально.
        vec2 toCamera = -i_Offset.xz;
        vec2 facing = dot(toCamera, toCamera) > 1e-8 ? normalize(toCamera) : vec2(0.0, 1.0);
        vec3 right = vec3(facing.y, 0.0, -facing.x);
        local = right * a_Position.x + vec3(0.0, a_Position.y, 0.0);
    }
    else
    {
        // Кубик сжимается до короба формы и лежит на земле его низом. Угол единичного куба — из вершины меша
        // (она — угол куба [0; 1]³ со смещённым центром основания, умноженный на сторону кубика).
        vec3 unit = a_Position / u_CubeSize + vec3(0.5, 0.0, 0.5);
        vec3 point = mix(i_BoxMin, i_BoxMax, unit);
        vec3 shaped = (point - vec3(0.5, i_BoxMin.y, 0.5)) * u_CubeSize;
        // Бока берут ту часть текстуры, что на их высоте (v = 1 − y, как у блоков): у полублока — нижнюю половину.
        if (a_Face > 0.5 && a_Face < 1.5)
            v_Uv.y = 1.0 - point.y;
        float c = cos(i_Yaw);
        float s = sin(i_Yaw);
        local = vec3(c * shaped.x + s * shaped.z, shaped.y, -s * shaped.x + c * shaped.z);
    }

    gl_Position = u_ViewProjection * vec4(i_Offset + local, 1.0);
    v_Distance = length(i_Offset + local);
}
