using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Engine.Graphics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Engine.Ui;

/// <summary>Как интерфейс смешивается с картинкой под ним.</summary>
public enum UiBlend
{
    /// <summary>Обычная прозрачность: альфа текстуры и цвета.</summary>
    Alpha,

    /// <summary>Инверсия фона (белый цвет даёт «1 − фон»): перекрестие видно и на небе, и на траве.</summary>
    Invert,
}

/// <summary>
/// Рисует <see cref="UiCanvas"/> поверх кадра: все четырёхугольники одним вызовом отрисовки, без буфера глубины
/// и отсечения граней. Две текстуры подключены сразу: атлас интерфейса (блок 0) и массив текстур блоков (блок 1).
/// </summary>
public sealed unsafe class SpriteBatch : IDisposable
{
    private readonly GL _gl;
    private readonly ShaderProgram _shader;
    private readonly VertexArrayObject _vao;
    private readonly GpuBuffer<UiVertex> _vertices;
    private GpuBuffer<uint>? _indices;
    private int _quadCapacity;

    public SpriteBatch(GL gl)
    {
        _gl = gl;
        _shader = new ShaderProgram(gl, ReadShader("ui.vert"), ReadShader("ui.frag"), "ui");
        _shader.SetUniform("u_Atlas", 0);
        _shader.SetUniform("u_Textures", 1);

        // Вершины меняются каждый кадр: StreamDraw подсказывает драйверу, что буфер заполняется заново и рисуется раз.
        _vertices = new GpuBuffer<UiVertex>(gl, [], BufferUsageARB.StreamDraw);
        _vao = new VertexArrayObject(gl);
        _vao.SetVertexBuffer(_vertices);
        _vao.SetAttribute(0, 2, VertexAttribType.Float, Offset(nameof(UiVertex.Position)));
        _vao.SetAttribute(1, 2, VertexAttribType.Float, Offset(nameof(UiVertex.Uv)));
        _vao.SetAttribute(2, 1, VertexAttribType.Float, Offset(nameof(UiVertex.Layer)));
        // Цвет — 4 байта, в шейдер приходит vec4 от 0 до 1.
        _vao.SetAttribute(3, 4, VertexAttribType.UnsignedByte, Offset(nameof(UiVertex.Color)), normalized: true);
        EnsureCapacity(256);
    }

    /// <param name="screenSize">Размер кадра в пикселях: координаты холста — в них же.</param>
    /// <param name="textures">Массив текстур для четырёхугольников со слоем ≥ 0; null — таких нет.</param>
    public void Draw(UiCanvas canvas, Vector2D<int> screenSize, Texture2D atlas, TextureArray? textures, UiBlend blend = UiBlend.Alpha)
    {
        var quads = canvas.QuadCount;
        if (quads == 0 || screenSize.X <= 0 || screenSize.Y <= 0)
            return;

        EnsureCapacity(quads);
        _vertices.SetData(canvas.Vertices, BufferUsageARB.StreamDraw);
        _vao.SetVertexBuffer(_vertices);

        _shader.Use();
        _shader.SetUniform("u_ScreenSize", new Vector2(screenSize.X, screenSize.Y));
        atlas.Bind(0);
        textures?.Bind(1);

        // Интерфейс лежит поверх мира: глубина не нужна, а отсекать нечего (Y экрана перевёрнут, обход граней тоже).
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        if (blend == UiBlend.Invert)
            _gl.BlendFunc(BlendingFactor.OneMinusDstColor, BlendingFactor.Zero);
        else
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _vao.DrawIndexed(quads * 6, quads * 4);

        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Буфер индексов на <paramref name="quads"/> четырёхугольников: по два треугольника (0, 1, 2) и (0, 2, 3).</summary>
    private void EnsureCapacity(int quads)
    {
        if (quads <= _quadCapacity)
            return;

        var capacity = Math.Max(quads, _quadCapacity * 2);
        var indices = new uint[capacity * 6];
        for (var q = 0; q < capacity; q++)
        {
            var v = (uint)(q * 4);
            (indices[q * 6], indices[q * 6 + 1], indices[q * 6 + 2]) = (v, v + 1, v + 2);
            (indices[q * 6 + 3], indices[q * 6 + 4], indices[q * 6 + 5]) = (v, v + 2, v + 3);
        }

        _indices?.Dispose();
        _indices = new GpuBuffer<uint>(_gl, indices);
        _vao.SetIndexBuffer(_indices);
        _quadCapacity = capacity;
    }

    private static uint Offset(string field) => (uint)Marshal.OffsetOf<UiVertex>(field);

    private static string ReadShader(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Engine.Ui.Shaders.{name}")
            ?? throw new InvalidOperationException($"В сборке нет шейдера интерфейса {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        _indices?.Dispose();
        _vertices.Dispose();
        _vao.Dispose();
        _shader.Dispose();
    }
}
