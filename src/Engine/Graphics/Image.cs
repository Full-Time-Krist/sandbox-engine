using StbImageSharp;

namespace Engine.Graphics;

/// <summary>Изображение в памяти: пиксели RGBA по 8 бит, строки сверху вниз.</summary>
public sealed class Image
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public Image(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"Ожидалось {width * height * 4} байт для {width}×{height} RGBA, получено {pixels.Length}.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Загружает PNG, JPG и т. п. и приводит его к RGBA.</summary>
    public static Image Load(string path)
    {
        using var stream = File.OpenRead(path);
        var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return new Image(result.Width, result.Height, result.Data);
    }

    /// <summary>Розово-чёрная шахматка «нет текстуры»: её сразу видно в мире.</summary>
    public static Image CreateMissing(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var magenta = ((x * 2 / width) + (y * 2 / height)) % 2 == 0;
            var i = (y * width + x) * 4;
            pixels[i + 0] = magenta ? (byte)255 : (byte)0;
            pixels[i + 1] = 0;
            pixels[i + 2] = magenta ? (byte)255 : (byte)0;
            pixels[i + 3] = 255;
        }

        return new Image(width, height, pixels);
    }
}
