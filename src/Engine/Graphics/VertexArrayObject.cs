using Silk.NET.OpenGL;

namespace Engine.Graphics;

/// <summary>
/// VAO: описывает, как читать вершины из буферов (какие атрибуты, с каким смещением)
/// и какой буфер индексов использовать.
/// </summary>
public sealed unsafe class VertexArrayObject : IDisposable
{
    private readonly GL _gl;
    private int _indexCount;

    public uint Handle { get; }

    public VertexArrayObject(GL gl)
    {
        _gl = gl;
        Handle = gl.CreateVertexArray();
    }

    /// <summary>Подключает буфер вершин к точке привязки <paramref name="binding"/>.</summary>
    public void SetVertexBuffer<T>(GpuBuffer<T> buffer, uint binding = 0) where T : unmanaged
    {
        _gl.VertexArrayVertexBuffer(Handle, binding, buffer.Handle, 0, (uint)sizeof(T));
    }

    /// <summary>
    /// Описывает атрибут вершины: <paramref name="location"/> соответствует layout(location = N) в шейдере,
    /// <paramref name="offset"/> задаёт смещение поля внутри структуры вершины в байтах.
    /// </summary>
    public void SetAttribute(uint location, int components, VertexAttribType type, uint offset, uint binding = 0, bool normalized = false)
    {
        _gl.EnableVertexArrayAttrib(Handle, location);
        _gl.VertexArrayAttribFormat(Handle, location, components, type, normalized, offset);
        _gl.VertexArrayAttribBinding(Handle, location, binding);
    }

    public void SetIndexBuffer(GpuBuffer<uint> buffer)
    {
        _gl.VertexArrayElementBuffer(Handle, buffer.Handle);
        _indexCount = buffer.Count;
    }

    public void DrawIndexed(PrimitiveType primitive = PrimitiveType.Triangles)
    {
        _gl.BindVertexArray(Handle);
        _gl.DrawElements(primitive, (uint)_indexCount, DrawElementsType.UnsignedInt, null);
    }

    /// <summary>Рисует вершины подряд, без буфера индексов.</summary>
    public void DrawArrays(PrimitiveType primitive, int vertexCount, int first = 0)
    {
        _gl.BindVertexArray(Handle);
        _gl.DrawArrays(primitive, first, (uint)vertexCount);
    }

    public void Dispose() => _gl.DeleteVertexArray(Handle);
}
