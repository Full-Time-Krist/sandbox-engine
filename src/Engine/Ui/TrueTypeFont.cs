using StbTrueTypeSharp;
using static StbTrueTypeSharp.StbTrueType;

namespace Engine.Ui;

/// <summary>Глиф, растеризованный в оттенки серого: покрытие пикселя от 0 до 255, строки сверху вниз.</summary>
/// <param name="OffsetX">Сдвиг левого края картинки от точки пера, в пикселях.</param>
/// <param name="OffsetY">Сдвиг верхнего края картинки от базовой линии (обычно отрицательный: глиф выше линии).</param>
public sealed record GlyphBitmap(int Width, int Height, int OffsetX, int OffsetY, byte[] Coverage);

/// <summary>
/// Шрифт TrueType в памяти: метрики, кернинг и растеризация глифов (порт stb_truetype). Без OpenGL —
/// растеризацию можно проверять тестами. Все размеры здесь — в единицах шрифта; в пиксели их переводит масштаб.
/// </summary>
public sealed unsafe class TrueTypeFont : IDisposable
{
    private readonly stbtt_fontinfo _info;

    /// <summary>
    /// Высота прописной буквы — контура «H» от низа до верха — в единицах шрифта: по ней подбирается размер текста.
    /// Именно высота контура, а не его верх: у некоторых пиксельных шрифтов (Press Start 2P) буквы стоят на клетку
    /// выше базовой линии, и по верху размер получился бы на клетку больше — сетка шрифта не легла бы на пиксели.
    /// </summary>
    public int CapHeight { get; }

    /// <summary>Подъём над базовой линией, спуск под ней (отрицательный) и промежуток между строками.</summary>
    public int Ascent { get; }
    public int Descent { get; }
    public int LineGap { get; }

    /// <exception cref="InvalidDataException">Данные — не шрифт TrueType.</exception>
    public TrueTypeFont(byte[] data)
    {
        // CreateFont держит массив закреплённым, пока шрифт не освобождён: stb читает данные по указателю.
        _info = CreateFont(data, 0) ?? throw new InvalidDataException("Файл не похож на шрифт TrueType.");

        int ascent, descent, lineGap;
        stbtt_GetFontVMetrics(_info, &ascent, &descent, &lineGap);
        (Ascent, Descent, LineGap) = (ascent, descent, lineGap);

        int x0, y0, x1, y1;
        // Если «H» в шрифте нет, высота прописной — около 70 % подъёма, как у большинства шрифтов.
        CapHeight = stbtt_GetCodepointBox(_info, 'H', &x0, &y0, &x1, &y1) != 0 && y1 > y0 ? y1 - y0 : (int)(ascent * 0.7);
    }

    public static TrueTypeFont Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Номер глифа для символа Unicode; 0 — символа в шрифте нет.</summary>
    public int FindGlyph(int codepoint) => stbtt_FindGlyphIndex(_info, codepoint);

    /// <summary>Сдвиг пера после глифа, в единицах шрифта.</summary>
    public int GetAdvance(int glyph)
    {
        int advance, leftBearing;
        stbtt_GetGlyphHMetrics(_info, glyph, &advance, &leftBearing);
        return advance;
    }

    /// <summary>Поправка расстояния между парой глифов (кернинг), в единицах шрифта.</summary>
    public int GetKerning(int first, int second) => stbtt_GetGlyphKernAdvance(_info, first, second);

    /// <summary>Растеризует глиф в масштабе <paramref name="scale"/> (пикселей на единицу шрифта).</summary>
    public GlyphBitmap Rasterize(int glyph, float scale)
    {
        int x0, y0, x1, y1;
        stbtt_GetGlyphBitmapBox(_info, glyph, scale, scale, &x0, &y0, &x1, &y1);
        var width = x1 - x0;
        var height = y1 - y0;
        var coverage = new byte[Math.Max(0, width * height)];
        if (width > 0 && height > 0)
        {
            fixed (byte* output = coverage)
                stbtt_MakeGlyphBitmap(_info, output, width, height, width, scale, scale, glyph);
        }
        return new GlyphBitmap(Math.Max(0, width), Math.Max(0, height), x0, y0, coverage);
    }

    public void Dispose() => _info.Dispose();
}
