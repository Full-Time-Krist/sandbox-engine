using System.Numerics;
using System.Runtime.InteropServices;
using Engine;
using Engine.Graphics;
using Engine.Windowing;
using Silk.NET.OpenGL;

namespace Client;

internal sealed class ClientApp(WindowSettings settings) : Application(settings)
{
    // Обычные поля, а не record struct: Marshal.OffsetOf ищет поле по имени,
    // а у позиционного record поля скрыты за авто-свойствами.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Vertex(Vector3 position, Vector3 color)
    {
        public readonly Vector3 Position = position;
        public readonly Vector3 Color = color;
    }

    private ShaderProgram _shader = null!;
    private GpuBuffer<Vertex> _vertices = null!;
    private GpuBuffer<uint> _indices = null!;
    private VertexArrayObject _vao = null!;
    private float _time;

    protected override void OnLoad()
    {
        Gl.ClearColor(0.53f, 0.81f, 0.92f, 1f);

        _shader = ShaderProgram.FromFiles(Gl, Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "triangle"));

        ReadOnlySpan<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0f), new Vector3(1f, 0f, 0f)),
            new(new Vector3( 0.5f, -0.5f, 0f), new Vector3(0f, 1f, 0f)),
            new(new Vector3( 0.0f,  0.5f, 0f), new Vector3(0f, 0f, 1f)),
        ];
        ReadOnlySpan<uint> indices = [0, 1, 2];

        _vertices = new GpuBuffer<Vertex>(Gl, vertices);
        _indices = new GpuBuffer<uint>(Gl, indices);

        _vao = new VertexArrayObject(Gl);
        _vao.SetVertexBuffer(_vertices);
        _vao.SetAttribute(0, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position)));
        _vao.SetAttribute(1, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Color)));
        _vao.SetIndexBuffer(_indices);
    }

    protected override void OnRender(double deltaTime)
    {
        _time += (float)deltaTime;

        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _shader.Use();
        _shader.SetUniform("u_Time", _time);
        _vao.DrawIndexed();
    }

    protected override void OnUnload()
    {
        _vao.Dispose();
        _indices.Dispose();
        _vertices.Dispose();
        _shader.Dispose();
    }
}
