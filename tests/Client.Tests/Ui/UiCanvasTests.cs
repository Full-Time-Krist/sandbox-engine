using System.Numerics;
using Engine.Ui;

namespace Client.Tests.Ui;

public sealed class UiCanvasTests : IDisposable
{
    private readonly TrueTypeFont _font = TrueTypeFont.Load(UiAtlasTests.FontPath);
    private readonly UiAtlas _atlas;
    private readonly UiCanvas _canvas;

    public UiCanvasTests()
    {
        _atlas = UiAtlas.Build(_font, 14, UiAtlas.DefaultCharacters);
        _canvas = new UiCanvas(_atlas);
    }

    public void Dispose() => _font.Dispose();

    private static UiColor Red => new(255, 0, 0, 128);

    [Fact]
    public void FillRect_IsOneQuad_FromWhiteSquareOfAtlas()
    {
        _canvas.FillRect(10, 20, 30, 40, Red);

        var v = _canvas.Vertices.ToArray();
        Assert.Equal(1, _canvas.QuadCount);
        Assert.Equal([new Vector2(10, 20), new Vector2(40, 20), new Vector2(40, 60), new Vector2(10, 60)], v.Select(x => x.Position));
        Assert.All(v, x => Assert.Equal(_atlas.WhiteUv, x.Uv));
        Assert.All(v, x => Assert.Equal(UiCanvas.AtlasLayer, x.Layer));
        Assert.All(v, x => Assert.Equal(Red.Packed, x.Color));
    }

    [Fact]
    public void Frame_CoversBorderExactlyOnce()
    {
        // Рамка 10 × 8 толщиной 2: каждый пиксель края покрыт ровно одной полосой, середина пуста.
        _canvas.DrawFrame(0, 0, 10, 8, 2, Red);

        var cover = new int[10, 8];
        for (var q = 0; q < _canvas.QuadCount; q++)
        {
            var (min, max) = (_canvas.Vertices[q * 4].Position, _canvas.Vertices[q * 4 + 2].Position);
            for (var x = (int)min.X; x < max.X; x++)
            for (var y = (int)min.Y; y < max.Y; y++)
                cover[x, y]++;
        }

        for (var x = 0; x < 10; x++)
        for (var y = 0; y < 8; y++)
        {
            var border = x < 2 || x >= 8 || y < 2 || y >= 6;
            Assert.Equal(border ? 1 : 0, cover[x, y]);
        }
    }

    [Fact]
    public void Text_GlyphsOnWholePixels_OnBaseline_SpacesDrawNothing()
    {
        _canvas.DrawText("Ж ж", 5, 7, UiColor.White);

        Assert.Equal(2, _canvas.QuadCount); // пробел — без четырёхугольника
        foreach (var v in _canvas.Vertices)
            Assert.True(v.Position.X == MathF.Round(v.Position.X) && v.Position.Y == MathF.Round(v.Position.Y), $"{v.Position} не на целом пикселе");

        // Нижний край прописной «Ж» — на базовой линии: верх строки + подъём шрифта.
        _atlas.TryGetGlyph('Ж', out var zhe);
        var bottom = _canvas.Vertices[2].Position.Y;
        Assert.Equal(7 + _atlas.Ascent + zhe.OffsetY + zhe.Height, bottom);
        Assert.Equal(5 + zhe.OffsetX, _canvas.Vertices[0].Position.X);

        // Вторая «ж» стоит дальше на ширину «Ж» и пробела.
        var second = _canvas.Vertices[4].Position.X;
        _atlas.TryGetGlyph('ж', out var small);
        Assert.Equal(5 + _atlas.MeasureWidth("Ж ") + small.OffsetX, second, 1);
    }

    [Fact]
    public void TexturedQuad_KeepsLayerAndCorners()
    {
        _canvas.DrawTexturedQuad(new(0, 0), new(4, 2), new(4, 6), new(0, 4), Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY,
            7, UiColor.White);

        Assert.All(_canvas.Vertices.ToArray(), v => Assert.Equal(7, v.Layer));
        Assert.Equal(new Vector2(4, 6), _canvas.Vertices[2].Position);
        Assert.Equal(Vector2.One, _canvas.Vertices[2].Uv);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        _canvas.FillRect(0, 0, 1, 1, Red);
        _canvas.Clear();

        Assert.Equal(0, _canvas.QuadCount);
    }

    [Fact]
    public void Color_PackedAsRgbaBytesInMemory()
    {
        var packed = new UiColor(1, 2, 3, 4).Packed;

        Assert.Equal([1, 2, 3, 4], BitConverter.GetBytes(packed));
        Assert.Equal(new UiColor(51, 102, 153, 200), new UiColor(255, 255, 255, 200).Shade(0.2f) with { G = 102, B = 153 });
        Assert.Equal(new UiColor(204, 204, 204), UiColor.White.Shade(0.8f));
        Assert.Equal(128, UiColor.White.WithAlpha(0.5f).A);
    }
}
