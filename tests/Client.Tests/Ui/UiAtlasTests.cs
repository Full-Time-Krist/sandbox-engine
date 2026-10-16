using Engine.Ui;

namespace Client.Tests.Ui;

/// <summary>Атлас интерфейса на настоящем шрифте игры (Assets/Fonts/ui.ttf).</summary>
public sealed class UiAtlasTests : IDisposable
{
    private const int Scale = 3;

    private readonly TrueTypeFont _font = TrueTypeFont.Load(FontPath);
    private readonly UiAtlas _atlas;

    public UiAtlasTests() => _atlas = UiAtlas.Build(_font, 7 * Scale, UiAtlas.DefaultCharacters);

    public void Dispose() => _font.Dispose();

    internal static string FontPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "ui.ttf");

    /// <summary>Есть ли символ в атласе своим глифом, а не заменой.</summary>
    private bool HasOwnGlyph(int codepoint) =>
        _atlas.TryGetGlyph(codepoint, out var glyph) && glyph.Index == _font.FindGlyph(codepoint) && glyph.Index != 0;

    [Fact]
    public void Font_HasEveryLetterAndDigit_RussianIncluded()
    {
        var required = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя" +
                       "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,:;!?-+()×";

        var missing = required.Where(c => !HasOwnGlyph(c)).ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void MissingCharacter_IsReplacedWithQuestionMark()
    {
        Assert.True(_atlas.TryGetGlyph('漢', out var glyph));
        Assert.True(_atlas.TryGetGlyph(UiAtlas.Replacement, out var replacement));
        Assert.Equal(replacement, glyph);
    }

    [Fact]
    public void Glyphs_LieInsideImage_AndDoNotOverlap()
    {
        var rects = UiAtlas.DefaultCharacters.Distinct().Where(HasOwnGlyph)
            .Select(c => { _atlas.TryGetGlyph(c, out var g); return (Char: c, Rect: PixelRectOf(g)); })
            .Where(r => r.Rect.W > 0)
            .ToList();
        Assert.True(rects.Count > 150);

        foreach (var (c, (x, y, w, h)) in rects)
        {
            Assert.True(x >= 0 && y >= 0 && x + w <= _atlas.Image.Width && y + h <= _atlas.Image.Height, $"'{(char)c}' вне атласа");
            // Белый квадрат заливки — в левом верхнем углу 2 × 2, глифы его не задевают.
            Assert.False(x < 2 && y < 2, $"'{(char)c}' на белом квадрате");
        }

        for (var i = 0; i < rects.Count; i++)
        for (var j = i + 1; j < rects.Count; j++)
        {
            var (a, b) = (rects[i].Rect, rects[j].Rect);
            var overlap = a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
            Assert.False(overlap, $"'{(char)rects[i].Char}' и '{(char)rects[j].Char}' перекрываются");
        }
    }

    [Fact]
    public void GlyphPixels_AreWhite_CoverageInAlpha_WhiteSquareIsOpaque()
    {
        var pixels = _atlas.Image.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Где есть покрытие, цвет белый: цвет текста задаёт вершина.
            if (pixels[i + 3] > 0)
                Assert.True(pixels[i] == 255 && pixels[i + 1] == 255 && pixels[i + 2] == 255, $"пиксель {i / 4} не белый");
        }

        var (u, v) = (_atlas.WhiteUv.X * _atlas.Image.Width, _atlas.WhiteUv.Y * _atlas.Image.Height);
        var white = ((int)v * _atlas.Image.Width + (int)u) * 4;
        Assert.Equal([255, 255, 255, 255], pixels[white..(white + 4)]);
    }

    [Fact]
    public void CapitalLetter_HasRequestedHeight()
    {
        _atlas.TryGetGlyph('H', out var h);

        Assert.InRange(h.Height, 7 * Scale - 1, 7 * Scale + 1);
        Assert.Equal(7 * Scale, _atlas.CapHeight);
    }

    /// <summary>
    /// Шрифт игры пиксельный: при высоте прописной 7 × масштаб каждая клетка его сетки — ровно квадрат пикселей,
    /// поэтому покрытие только 0 или 255, без полутонов. Полутона значили бы сдвиг на долю пикселя или дробный размер —
    /// буквы бы размылись. Сменили шрифт на сглаженный — этот тест нужно убрать.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PixelFont_AtIntegerScale_IsCrisp(int scale)
    {
        var atlas = UiAtlas.Build(_font, 7 * scale, UiAtlas.DefaultCharacters);

        var halftones = atlas.Image.Pixels.Where((b, i) => i % 4 == 3 && b is > 0 and < 255).Count();

        Assert.Equal(0, halftones);
    }

    [Fact]
    public void Width_IsSumOfAdvancesAndKerning()
    {
        _atlas.TryGetGlyph('A', out var a);
        _atlas.TryGetGlyph('V', out var v);

        Assert.Equal((int)MathF.Round(a.Advance + _atlas.Kerning(a, v) + v.Advance), _atlas.MeasureWidth("AV"));
        Assert.Equal(0, _atlas.MeasureWidth(""));
    }

    [Fact]
    public void NotAFont_IsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => new TrueTypeFont(new byte[100]));
    }

    private (int X, int Y, int W, int H) PixelRectOf(AtlasGlyph g) =>
        ((int)MathF.Round(g.UvMin.X * _atlas.Image.Width), (int)MathF.Round(g.UvMin.Y * _atlas.Image.Height), g.Width, g.Height);
}
