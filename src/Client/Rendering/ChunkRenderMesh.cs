using System.Runtime.InteropServices;
using Engine.Graphics;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Меш чанка в видеопамяти: буферы вершин и индексов и VAO с раскладкой <see cref="ChunkVertex"/>.</summary>
internal sealed class ChunkRenderMesh : IDisposable
{
    private readonly GpuBuffer<ChunkVertex> _vertices;
    private readonly GpuBuffer<uint> _indices;
    private readonly VertexArrayObject _vao;

    public ChunkRenderMesh(GL gl, ChunkMeshData data)
    {
        _vertices = new GpuBuffer<ChunkVertex>(gl, CollectionsMarshal.AsSpan(data.Vertices));
        _indices = new GpuBuffer<uint>(gl, CollectionsMarshal.AsSpan(data.Indices));

        _vao = new VertexArrayObject(gl);
        _vao.SetVertexBuffer(_vertices);
        _vao.SetAttribute(0, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<ChunkVertex>(nameof(ChunkVertex.Position)));
        _vao.SetAttribute(1, 2, VertexAttribType.Float, (uint)Marshal.OffsetOf<ChunkVertex>(nameof(ChunkVertex.Uv)));
        _vao.SetAttribute(2, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<ChunkVertex>(nameof(ChunkVertex.Layer)));
        _vao.SetAttribute(3, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<ChunkVertex>(nameof(ChunkVertex.Shade)));
        _vao.SetAttribute(4, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<ChunkVertex>(nameof(ChunkVertex.Sky)));
        _vao.SetAttribute(5, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<ChunkVertex>(nameof(ChunkVertex.BlockLight)));
        _vao.SetIndexBuffer(_indices);
    }

    public void Draw() => _vao.DrawIndexed();

    public void Dispose()
    {
        _vao.Dispose();
        _indices.Dispose();
        _vertices.Dispose();
    }
}
