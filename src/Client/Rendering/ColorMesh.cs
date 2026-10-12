using System.Runtime.InteropServices;
using Engine.Graphics;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Меш из цветных вершин: буфер вершин, необязательный буфер индексов и VAO.</summary>
internal sealed class ColorMesh : IDisposable
{
    private readonly GpuBuffer<ColorVertex> _vertices;
    private readonly GpuBuffer<uint>? _indices;
    private readonly VertexArrayObject _vao;
    private readonly PrimitiveType _primitive;

    public ColorMesh(GL gl, ReadOnlySpan<ColorVertex> vertices, ReadOnlySpan<uint> indices, PrimitiveType primitive)
    {
        _primitive = primitive;
        _vertices = new GpuBuffer<ColorVertex>(gl, vertices);

        _vao = new VertexArrayObject(gl);
        _vao.SetVertexBuffer(_vertices);
        _vao.SetAttribute(0, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<ColorVertex>(nameof(ColorVertex.Position)));
        _vao.SetAttribute(1, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<ColorVertex>(nameof(ColorVertex.Color)));

        if (!indices.IsEmpty)
        {
            _indices = new GpuBuffer<uint>(gl, indices);
            _vao.SetIndexBuffer(_indices);
        }
    }

    public void Draw()
    {
        if (_indices is not null)
            _vao.DrawIndexed(_primitive);
        else
            _vao.DrawArrays(_primitive, _vertices.Count);
    }

    public void Dispose()
    {
        _vao.Dispose();
        _indices?.Dispose();
        _vertices.Dispose();
    }
}
