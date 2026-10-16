using System.Numerics;
using Engine.Graphics;

namespace Engine.Ui;

/// <summary>Глиф в атласе: где лежит его картинка и как ставить её относительно пера.</summary>
/// <param name="Index">Номер глифа в шрифте (для кернинга).</param>
/// <param name="Advance">Сдвиг пера после глифа, в пикселях (дробный: округляется положение каждого глифа, а не сдвиг).</param>
/// <param name="OffsetX">Левый край картинки от пера, в пикселях.</param>
/// <param name="OffsetY">Верхний край картинки от базовой линии, в пикселях (обычно отрицательный).</param>
/// <param name="UvMin">Левый верхний угол картинки в атласе.</param>
/// <param name="UvMax">Правый нижний угол картинки в атласе.</param>
public readonly record struct AtlasGlyph(int Index, float Advance, int OffsetX, int OffsetY, int Width, int Height, Vector2 UvMin, Vector2 UvMax);

/// <summary>
/// Атлас интерфейса: глифы одного шрифта одного размера и белый квадрат для заливки — в одной картинке RGBA.
/// Глифы белые, покрытие — в альфе: цвет текста и заливки задаёт вершина, поэтому весь интерфейс рисуется
/// одной текстурой. Без OpenGL: картинку в видеопамять загружает <see cref="Texture2D"/>.
/// </summary>
/// <remarks>
/// Размер шрифта задаётся высотой прописной буквы в пикселях, а не кеглем: у разных шрифтов при одном кегле
/// буквы разной высоты, а так любой шрифт из файла выглядит одного роста. Пиксельный шрифт, нарисованный по сетке
/// с прописной в 7 клеток, при высоте 7 × масштаб ложится ровно на пиксели экрана.
/// </remarks>
public sealed class UiAtlas
{
    /// <summary>Символы, которые попадают в атлас: латиница с цифрами и знаками, кириллица, немного типографики.</summary>
    public static IEnumerable<int> DefaultCharacters =>
        Enumerable.Range(0x20, 0x7F - 0x20)              // ASCII от пробела до тильды
            .Concat(Enumerable.Range(0x410, 0x40))       // А–я
            .Concat([0x401, 0x451])                      // Ё ё
            .Concat([0xD7, 0xAB, 0xBB, 0x2014, 0x2116, 0x2026]); // × « » — № …

    /// <summary>Чем заменяется символ, которого нет в атласе.</summary>
    public const char Replacement = '?';

    /// <summary>Отступ между картинками в атласе: соседние глифы не касаются друг друга.</summary>
    private const int Padding = 1;

    private readonly Dictionary<int, AtlasGlyph> _glyphs;
    private readonly TrueTypeFont _font;
    private readonly float _scale;

    public Image Image { get; }

    /// <summary>Текстурная координата середины белого квадрата: прямоугольники заливки берут цвет оттуда.</summary>
    public Vector2 WhiteUv { get; }

    /// <summary>Высота прописной буквы, в пикселях.</summary>
    public int CapHeight { get; }

    /// <summary>От верха строки до базовой линии.</summary>
    public int Ascent { get; }

    /// <summary>Шаг строк: от верха строки до верха следующей.</summary>
    public int LineHeight { get; }

    private UiAtlas(Image image, Vector2 whiteUv, Dictionary<int, AtlasGlyph> glyphs, TrueTypeFont font, float scale, int capHeight)
    {
        Image = image;
        WhiteUv = whiteUv;
        _glyphs = glyphs;
        _font = font;
        _scale = scale;
        CapHeight = capHeight;
        Ascent = (int)MathF.Round(font.Ascent * scale);
        LineHeight = (int)MathF.Round((font.Ascent - font.Descent + font.LineGap) * scale);
    }

    /// <summary>Растеризует символы шрифта так, чтобы прописная буква была высотой <paramref name="capHeight"/> пикселей.</summary>
    /// <param name="font">Шрифт должен жить, пока жив атлас: по нему считается кернинг.</param>
    public static UiAtlas Build(TrueTypeFont font, int capHeight, IEnumerable<int> characters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capHeight);
        var scale = (float)capHeight / font.CapHeight;

        // Растеризуем всё, что есть в шрифте; символов, которых нет, в атласе не будет (их заменит Replacement).
        var bitmaps = new List<(int Codepoint, int Glyph, GlyphBitmap Bitmap)>();
        foreach (var codepoint in characters.Distinct())
        {
            var glyph = font.FindGlyph(codepoint);
            if (glyph != 0 || codepoint == ' ')
                bitmaps.Add((codepoint, glyph, font.Rasterize(glyph, scale)));
        }

        // Белый квадрат 2×2 — первым, остальное раскладываем полками: самые высокие картинки — первыми.
        const int WhiteSize = 2;
        var order = bitmaps.OrderByDescending(b => b.Bitmap.Height).ThenBy(b => b.Codepoint).ToList();
        var area = order.Sum(b => (b.Bitmap.Width + Padding) * (b.Bitmap.Height + Padding)) + (WhiteSize + Padding) * (WhiteSize + Padding);
        var width = (int)Math.Max(64, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Ceiling(Math.Sqrt(area * 1.3))));

        var places = new (int X, int Y)[order.Count];
        int x = WhiteSize + Padding, y = 0, shelfHeight = WhiteSize;
        for (var i = 0; i < order.Count; i++)
        {
            var bitmap = order[i].Bitmap;
            if (x + bitmap.Width > width)
            {
                y += shelfHeight + Padding;
                x = 0;
                shelfHeight = 0;
            }
            places[i] = (x, y);
            x += bitmap.Width + Padding;
            shelfHeight = Math.Max(shelfHeight, bitmap.Height);
        }
        var height = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(y + shelfHeight));

        var pixels = new byte[width * height * 4];
        for (var py = 0; py < WhiteSize; py++)
        for (var px = 0; px < WhiteSize; px++)
            pixels.AsSpan((py * width + px) * 4, 4).Fill(255);

        var glyphs = new Dictionary<int, AtlasGlyph>();
        for (var i = 0; i < order.Count; i++)
        {
            var (codepoint, glyph, bitmap) = order[i];
            var (gx, gy) = places[i];
            for (var row = 0; row < bitmap.Height; row++)
            for (var col = 0; col < bitmap.Width; col++)
            {
                var p = ((gy + row) * width + gx + col) * 4;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = 255;
                pixels[p + 3] = bitmap.Coverage[row * bitmap.Width + col];
            }

            glyphs[codepoint] = new AtlasGlyph(glyph, font.GetAdvance(glyph) * scale, bitmap.OffsetX, bitmap.OffsetY,
                bitmap.Width, bitmap.Height,
                new Vector2((float)gx / width, (float)gy / height),
                new Vector2((float)(gx + bitmap.Width) / width, (float)(gy + bitmap.Height) / height));
        }

        var whiteUv = new Vector2((float)WhiteSize / 2 / width, (float)WhiteSize / 2 / height);
        return new UiAtlas(new Image(width, height, pixels), whiteUv, glyphs, font, scale, capHeight);
    }

    /// <summary>Глиф символа; символ, которого нет в атласе, заменяется на <see cref="Replacement"/>.</summary>
    public bool TryGetGlyph(int codepoint, out AtlasGlyph glyph) =>
        _glyphs.TryGetValue(codepoint, out glyph) || _glyphs.TryGetValue(Replacement, out glyph);

    /// <summary>Кернинг между двумя глифами, в пикселях.</summary>
    public float Kerning(in AtlasGlyph first, in AtlasGlyph second) => _font.GetKerning(first.Index, second.Index) * _scale;

    /// <summary>
    /// Раскладывает строку: для каждого символа — глиф и положение пера (левый край, целый пиксель).
    /// Перо копится дробным, а округляется положение каждого глифа: иначе ошибки округления складывались бы
    /// и длинная строка уезжала бы. Возвращает ширину строки.
    /// </summary>
    public int Layout(ReadOnlySpan<char> text, List<(AtlasGlyph Glyph, int PenX)>? output)
    {
        var pen = 0f;
        AtlasGlyph? previous = null;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!TryGetGlyph(rune.Value, out var glyph))
                continue;
            if (previous is { } prev)
                pen += Kerning(prev, glyph);
            output?.Add((glyph, (int)MathF.Round(pen)));
            pen += glyph.Advance;
            previous = glyph;
        }
        return (int)MathF.Round(pen);
    }

    /// <summary>Ширина строки в пикселях.</summary>
    public int MeasureWidth(ReadOnlySpan<char> text) => Layout(text, null);
}
