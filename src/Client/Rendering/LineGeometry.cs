using System.Numerics;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Линейная геометрия интерфейса: рамка выделенного блока и перекрестие.</summary>
internal static class LineGeometry
{
    /// <summary>12 рёбер куба [0; 1]³.</summary>
    public static ColorMesh CreateBoxOutline(GL gl, Vector3 color)
    {
        var corners = new Vector3[8];
        for (var i = 0; i < 8; i++)
            corners[i] = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);

        // Ребро соединяет углы, номера которых отличаются ровно одним битом (одной координатой).
        var vertices = new List<ColorVertex>(24);
        for (var a = 0; a < 8; a++)
        for (var bit = 1; bit < 8; bit <<= 1)
        {
            var b = a | bit;
            if (b == a)
                continue;
            vertices.Add(new ColorVertex(corners[a], color));
            vertices.Add(new ColorVertex(corners[b], color));
        }

        return new ColorMesh(gl, [.. vertices], [], PrimitiveType.Lines);
    }

    /// <summary>
    /// Перекрестие из прямоугольников, координаты в пикселях от центра экрана; в NDC их переводит матрица масштаба.
    /// Прямоугольники, а не линии: в core-профиле OpenGL толщина линий больше 1 пикселя не гарантирована.
    /// </summary>
    public static ColorMesh CreateCrosshair(GL gl, float halfLength = 10f, float halfThickness = 1f)
    {
        var vertices = new List<ColorVertex>();
        var indices = new List<uint>();

        // Вертикальная черта разбита на две части, чтобы не перекрывать горизонтальную в центре:
        // при инвертирующем смешивании двойная инверсия вернула бы там исходный цвет фона.
        AddRect(-halfLength, -halfThickness, halfLength, halfThickness);
        AddRect(-halfThickness, halfThickness, halfThickness, halfLength);
        AddRect(-halfThickness, -halfLength, halfThickness, -halfThickness);

        return new ColorMesh(gl, [.. vertices], [.. indices], PrimitiveType.Triangles);

        void AddRect(float x0, float y0, float x1, float y1)
        {
            var first = (uint)vertices.Count;
            vertices.Add(new ColorVertex(new Vector3(x0, y0, 0), Vector3.One));
            vertices.Add(new ColorVertex(new Vector3(x1, y0, 0), Vector3.One));
            vertices.Add(new ColorVertex(new Vector3(x1, y1, 0), Vector3.One));
            vertices.Add(new ColorVertex(new Vector3(x0, y1, 0), Vector3.One));
            // Против часовой стрелки (Y в NDC направлен вверх), иначе отсечение задних граней скроет перекрестие.
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
    }
}
