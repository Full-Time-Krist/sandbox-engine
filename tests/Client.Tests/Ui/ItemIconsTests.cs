using Shared.Content;
using System.Numerics;
using Client.Rendering;
using Client.Ui;
using Engine.Ui;
using Shared.Blocks;
using Shared.Items;

namespace Client.Tests.Ui;

public sealed class ItemIconsTests : IDisposable
{
    private readonly BlockRegistry _blocks;
    private readonly ItemRegistry _items;
    private readonly BlockVisuals _visuals;
    private readonly TrueTypeFont _font = TrueTypeFont.Load(UiAtlasTests.FontPath);
    private readonly UiCanvas _canvas;

    public ItemIconsTests()
    {
        var content = GameContent.Load();
        _blocks = content.Blocks;
        _items = content.Items;
        _visuals = new BlockVisuals(_blocks, BlockVisuals.AppearancesFrom(content.BlockTextures));
        _canvas = new UiCanvas(UiAtlas.Build(_font, 7, UiAtlas.DefaultCharacters));
    }

    public void Dispose() => _font.Dispose();

    private int Layer(string texture) => _visuals.TextureNames.ToList().IndexOf(texture);

    private UiVertex[] DrawGrass(int scale, float x = 100, float y = 50)
    {
        ItemIcons.Draw(_canvas, _items.Get("base:grass"), _visuals, x, y, scale);
        return _canvas.Vertices.ToArray();
    }

    [Fact]
    public void BlockIcon_IsThreeFaces_TopAndSidesWithWorldTexturesAndShading()
    {
        var v = DrawGrass(scale: 2);

        Assert.Equal(3, _canvas.QuadCount);
        // Верх — текстура верха дёрна, яркость как у верхней грани в мире (1,0); южный бок — 0,8; восточный — 0,6.
        Assert.Equal((Layer("base:grass_top"), 255), (v[0].Layer, (int)(v[0].Color & 0xFF)));
        Assert.Equal((Layer("base:grass_side"), 204), (v[4].Layer, (int)(v[4].Color & 0xFF)));
        Assert.Equal((Layer("base:grass_side"), 153), (v[8].Layer, (int)(v[8].Color & 0xFF)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void BlockIcon_FitsItsSquare_OnWholePixels(int scale)
    {
        var size = ItemIcons.Size * scale;

        var v = DrawGrass(scale);

        Assert.All(v, x =>
        {
            Assert.InRange(x.Position.X, 100, 100 + size);
            Assert.InRange(x.Position.Y, 50, 50 + size);
            Assert.Equal(MathF.Round(x.Position.X), x.Position.X);
            Assert.Equal(MathF.Round(x.Position.Y), x.Position.Y);
        });
        // Кубик занимает квадрат целиком: верхняя точка, нижняя, левый и правый края.
        Assert.Equal(50, v.Min(x => x.Position.Y));
        Assert.Equal(50 + size, v.Max(x => x.Position.Y));
        Assert.Equal(100, v.Min(x => x.Position.X));
        Assert.Equal(100 + size, v.Max(x => x.Position.X));
    }

    [Fact]
    public void Sides_HaveTextureTopAtBlockTop_TopFaceFarCornerAtTop()
    {
        var v = DrawGrass(scale: 1, x: 0, y: 0);

        // Верх: угол с UV (0, 0) — дальний (северо-западный), он вверху ромба.
        Assert.Equal(new Vector2(8, 0), v.Take(4).Single(x => x.Uv == Vector2.Zero).Position);
        // Бока: строка v = 0 (верх картинки, полоса травы) — у верхнего ребра, v = 1 — у нижнего.
        foreach (var side in new[] { v.Skip(4).Take(4), v.Skip(8).Take(4) })
        {
            var topEdge = side.Where(x => x.Uv.Y == 0).Max(x => x.Position.Y);
            var bottomEdge = side.Where(x => x.Uv.Y == 1).Min(x => x.Position.Y);
            Assert.True(topEdge < bottomEdge);
        }
    }

    [Fact]
    public void NotBlockItem_IsMissingTextureSquare()
    {
        var items = new ItemRegistry();
        var stick = items.Register("base:stick", 64);

        ItemIcons.Draw(_canvas, stick, _visuals, 0, 0, 2);

        Assert.Equal(1, _canvas.QuadCount);
        Assert.All(_canvas.Vertices.ToArray(), v => Assert.Equal(0, v.Layer));
    }
}
