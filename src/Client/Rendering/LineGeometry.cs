using System.Numerics;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Линейная геометрия в мире: рамка выделенного блока. Перекрестие — в интерфейсе (<see cref="Ui.Hud"/>).</summary>
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
}
