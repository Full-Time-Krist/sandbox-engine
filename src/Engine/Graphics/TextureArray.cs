using Silk.NET.OpenGL;

namespace Engine.Graphics;

/// <summary>
/// Массив текстур (GL_TEXTURE_2D_ARRAY): набор слоёв одного размера, слой выбирается третьей координатой.
/// </summary>
/// <remarks>
/// В отличие от атласа, слои не соприкасаются, поэтому соседние текстуры не протекают на краях и mip-карты
/// строятся правильно. Повтор текстуры (wrap = repeat) работает внутри слоя, что позже позволит greedy meshing.
/// <para>
/// Строки изображения загружаются сверху вниз без переворота, поэтому v = 0 — это верх картинки.
/// </para>
/// </remarks>
public sealed unsafe class TextureArray : IDisposable
{
    private readonly GL _gl;

    public uint Handle { get; }
    public int Width { get; }
    public int Height { get; }
    public int Layers { get; }

    /// <param name="mipmaps">
    /// Строить ли mip-карты. Без них — для развёрток моделей (мобы): там рядом лежат куски разных граней и прозрачный фон,
    /// и уменьшенные уровни смешивали бы их — вдали по рёбрам моба шла бы чужая кайма.
    /// </param>
    public TextureArray(GL gl, IReadOnlyList<Image> layers, bool mipmaps = true)
    {
        if (layers.Count == 0)
            throw new ArgumentException("Нужен хотя бы один слой.", nameof(layers));

        _gl = gl;
        Width = layers[0].Width;
        Height = layers[0].Height;
        Layers = layers.Count;

        for (var i = 1; i < layers.Count; i++)
        {
            if (layers[i].Width != Width || layers[i].Height != Height)
                throw new ArgumentException($"Слой {i}: {layers[i].Width}×{layers[i].Height}, а все слои должны быть {Width}×{Height}.", nameof(layers));
        }

        // Число уровней mip-карт: 16 → 8 → 4 → 2 → 1, то есть log2(16) + 1 = 5.
        var levels = mipmaps ? (uint)(Math.Log2(Math.Max(Width, Height)) + 1) : 1u;

        Handle = gl.CreateTexture(TextureTarget.Texture2DArray);
        // Неизменяемое хранилище: размер и формат задаются один раз, драйверу не нужно ничего проверять при отрисовке.
        gl.TextureStorage3D(Handle, levels, SizedInternalFormat.Rgba8, (uint)Width, (uint)Height, (uint)Layers);

        for (var i = 0; i < layers.Count; i++)
        {
            fixed (byte* pixels = layers[i].Pixels)
            {
                gl.TextureSubImage3D(Handle, 0, 0, 0, i, (uint)Width, (uint)Height, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            }
        }

        if (mipmaps)
            gl.GenerateTextureMipmap(Handle);

        // Вблизи пиксели остаются чёткими квадратами (nearest). Вдали выбирается ближайший пиксель
        // и смешиваются два соседних уровня mip-карт: без мерцания, но без размытия пиксель-арта.
        gl.TextureParameter(Handle, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TextureParameter(Handle, TextureParameterName.TextureMinFilter,
            (int)(mipmaps ? TextureMinFilter.NearestMipmapLinear : TextureMinFilter.Nearest));
        gl.TextureParameter(Handle, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        gl.TextureParameter(Handle, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
    }

    public void Bind(uint unit) => _gl.BindTextureUnit(unit, Handle);

    public void Dispose() => _gl.DeleteTexture(Handle);
}
