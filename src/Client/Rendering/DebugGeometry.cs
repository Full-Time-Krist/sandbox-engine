using System.Numerics;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Временная геометрия для проверки камеры. Уйдёт, когда появятся чанки.</summary>
internal static class DebugGeometry
{
    /// <summary>
    /// Куб [0; 1]³. Цвета граней по осям: X красный, Y зелёный, Z синий;
    /// положительная сторона яркая, отрицательная тёмная.
    /// </summary>
    public static ColorMesh CreateCube(GL gl)
    {
        // Углы каждой грани перечислены против часовой стрелки, если смотреть снаружи куба.
        ReadOnlySpan<(Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector3 Color)> faces =
        [
            (new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1), new(1.0f, 0.2f, 0.2f)), // +X
            (new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0), new(0.5f, 0.1f, 0.1f)), // −X
            (new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0), new(0.2f, 1.0f, 0.2f)), // +Y
            (new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1), new(0.1f, 0.5f, 0.1f)), // −Y
            (new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1), new(0.2f, 0.4f, 1.0f)), // +Z
            (new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0), new(0.1f, 0.2f, 0.5f)), // −Z
        ];

        Span<ColorVertex> vertices = stackalloc ColorVertex[faces.Length * 4];
        Span<uint> indices = stackalloc uint[faces.Length * 6];

        for (var i = 0; i < faces.Length; i++)
        {
            var (a, b, c, d, color) = faces[i];
            var v = i * 4;
            vertices[v + 0] = new ColorVertex(a, color);
            vertices[v + 1] = new ColorVertex(b, color);
            vertices[v + 2] = new ColorVertex(c, color);
            vertices[v + 3] = new ColorVertex(d, color);

            // Квадрат из двух треугольников с тем же обходом: (A, B, C) и (A, C, D).
            var n = i * 6;
            indices[n + 0] = (uint)v;
            indices[n + 1] = (uint)(v + 1);
            indices[n + 2] = (uint)(v + 2);
            indices[n + 3] = (uint)v;
            indices[n + 4] = (uint)(v + 2);
            indices[n + 5] = (uint)(v + 3);
        }

        return new ColorMesh(gl, vertices, indices, PrimitiveType.Triangles);
    }

    /// <summary>Сетка линий на плоскости Y = 0, ось X красная, ось Z синяя.</summary>
    public static ColorMesh CreateGrid(GL gl, int halfSize)
    {
        var lineColor = new Vector3(0.35f);
        var xAxisColor = new Vector3(0.9f, 0.2f, 0.2f);
        var zAxisColor = new Vector3(0.2f, 0.3f, 0.9f);

        var vertices = new List<ColorVertex>();
        for (var i = -halfSize; i <= halfSize; i++)
        {
            // Линия, параллельная X (при i = 0 это сама ось X).
            vertices.Add(new ColorVertex(new Vector3(-halfSize, 0, i), i == 0 ? xAxisColor : lineColor));
            vertices.Add(new ColorVertex(new Vector3(halfSize, 0, i), i == 0 ? xAxisColor : lineColor));
            // Линия, параллельная Z.
            vertices.Add(new ColorVertex(new Vector3(i, 0, -halfSize), i == 0 ? zAxisColor : lineColor));
            vertices.Add(new ColorVertex(new Vector3(i, 0, halfSize), i == 0 ? zAxisColor : lineColor));
        }

        return new ColorMesh(gl, [.. vertices], [], PrimitiveType.Lines);
    }
}
