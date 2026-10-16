using Silk.NET.OpenGL;

namespace Engine.Graphics;

/// <summary>
/// Обычная двумерная текстура RGBA без mip-карт: для атласа интерфейса, который рисуется пиксель в пиксель.
/// </summary>
/// <remarks>Строки изображения загружаются сверху вниз без переворота, поэтому v = 0 — это верх картинки.</remarks>
public sealed unsafe class Texture2D : IDisposable
{
    private readonly GL _gl;

    public uint Handle { get; }
    public int Width { get; }
    public int Height { get; }

    public Texture2D(GL gl, Image image)
    {
        _gl = gl;
        Width = image.Width;
        Height = image.Height;

        Handle = gl.CreateTexture(TextureTarget.Texture2D);
        gl.TextureStorage2D(Handle, 1, SizedInternalFormat.Rgba8, (uint)Width, (uint)Height);
        fixed (byte* pixels = image.Pixels)
            gl.TextureSubImage2D(Handle, 0, 0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        // Интерфейс рисуется в целом масштабе, текселю соответствует целое число пикселей: сглаживание не нужно.
        gl.TextureParameter(Handle, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TextureParameter(Handle, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TextureParameter(Handle, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TextureParameter(Handle, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    public void Bind(uint unit) => _gl.BindTextureUnit(unit, Handle);

    public void Dispose() => _gl.DeleteTexture(Handle);
}
