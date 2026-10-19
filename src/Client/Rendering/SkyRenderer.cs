using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Graphics;
using Shared.Lighting;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>
/// Солнце и луна — квадраты-картинки на небесной сфере (слои массива текстур блоков). Рисуются первыми, сразу после
/// очистки кадра, без глубины: всё, что есть в мире, ляжет поверх. Камера всегда в начале координат, поэтому квадрат
/// на постоянном расстоянии по направлению светила движется вместе с глазом и выглядит бесконечно далёким.
/// </summary>
/// <remarks>
/// Солнце идёт по дуге с востока (+X) через зенит на запад (−X), ось дуги — север–юг (Z); луна — напротив солнца.
/// Ниже горизонта светило не рисуется: там вдали — туман цвета неба, и солнце было бы видно «под землёй».
/// </remarks>
internal sealed class SkyRenderer : IDisposable
{
    /// <summary>Расстояние до квадрата: внутри дальней плоскости камеры, ближе любого чанка за туманом не важно — глубины нет.</summary>
    private const float Distance = 100;

    /// <summary>Половина стороны: солнце около 7° на небе.</summary>
    private const float SunHalfSize = 6;
    private const float MoonHalfSize = 4.5f;

    private readonly GL _gl;
    private readonly ShaderProgram _shader;
    private readonly GpuBuffer<Vector2> _corners;
    private readonly VertexArrayObject _vao;
    private readonly int _sunLayer;
    private readonly int _moonLayer;

    public SkyRenderer(GL gl, ShaderProgram shader, int sunLayer, int moonLayer)
    {
        _gl = gl;
        _shader = shader;
        _sunLayer = sunLayer;
        _moonLayer = moonLayer;
        // Два треугольника квадрата [−1; 1]²; обход не важен — отсечение граней на время неба выключено.
        Vector2[] corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, -1), new(1, 1), new(-1, 1)];
        _corners = new GpuBuffer<Vector2>(gl, corners);
        _vao = new VertexArrayObject(gl);
        _vao.SetVertexBuffer(_corners);
        _vao.SetAttribute(0, 2, VertexAttribType.Float, 0);
    }

    /// <summary>Направление на солнце по углу над восточным горизонтом (<see cref="DayCycle.SunAngle"/>).</summary>
    public static Vector3 SunDirection(double worldTime)
    {
        var angle = DayCycle.SunAngle(worldTime);
        return new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0);
    }

    /// <summary>Видно ли светило в этом направлении: выше горизонта хотя бы краем.</summary>
    public static bool AboveHorizon(Vector3 direction, float halfSize) => direction.Y > -halfSize / Distance;

    public void Draw(Matrix4x4 viewProjection, double worldTime)
    {
        var sun = SunDirection(worldTime);
        _shader.Use();
        _shader.SetUniform("u_ViewProjection", viewProjection);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        DrawBody(sun, SunHalfSize, _sunLayer);
        DrawBody(-sun, MoonHalfSize, _moonLayer);

        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
    }

    private void DrawBody(Vector3 direction, float halfSize, int layer)
    {
        if (!AboveHorizon(direction, halfSize))
            return;
        // Плоскость квадрата перпендикулярна направлению: одна его ось — север–юг, другая — вдоль дуги.
        var across = Vector3.UnitZ;
        var along = Vector3.Cross(direction, across);
        _shader.SetUniform("u_Center", direction * Distance);
        _shader.SetUniform("u_Right", across * halfSize);
        _shader.SetUniform("u_Up", along * halfSize);
        _shader.SetUniform("u_Layer", (float)layer);
        _vao.DrawArrays(PrimitiveType.Triangles, 6);
    }

    public void Dispose()
    {
        _vao.Dispose();
        _corners.Dispose();
    }
}
