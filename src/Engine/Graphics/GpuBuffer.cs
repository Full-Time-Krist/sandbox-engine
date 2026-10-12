using Silk.NET.OpenGL;

namespace Engine.Graphics;

/// <summary>
/// Буфер в видеопамяти (вершины, индексы). Типизирован структурой элемента,
/// чтобы размеры в байтах считались автоматически.
/// </summary>
public sealed unsafe class GpuBuffer<T> : IDisposable where T : unmanaged
{
    private readonly GL _gl;

    public uint Handle { get; }
    public int Count { get; private set; }

    public GpuBuffer(GL gl, ReadOnlySpan<T> data, BufferUsageARB usage = BufferUsageARB.StaticDraw)
    {
        _gl = gl;
        Handle = gl.CreateBuffer();
        SetData(data, usage);
    }

    /// <summary>Заменяет всё содержимое буфера (размер может меняться).</summary>
    public void SetData(ReadOnlySpan<T> data, BufferUsageARB usage = BufferUsageARB.StaticDraw)
    {
        fixed (T* ptr = data)
            _gl.NamedBufferData(Handle, (nuint)(data.Length * sizeof(T)), ptr, usage);
        Count = data.Length;
    }

    public void Dispose() => _gl.DeleteBuffer(Handle);
}
