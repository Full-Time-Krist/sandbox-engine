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
    private readonly ItemVisuals _visuals;
    private readonly TrueTypeFont _font = TrueTypeFont.Load(UiAtlasTests.FontPath);
    private readonly UiCanvas _canvas;

    public ItemIconsTests()
    {
        var content = GameContent.Load();
        _blocks = content.Blocks;
        _items = content.Items;
        _visuals = new ItemVisuals(new BlockVisuals(_blocks, BlockVisuals.AppearancesFrom(content.BlockTextures)), _items, content.ItemIcons);
        _canvas = new UiCanvas(UiAtlas.Build(_font, 7, UiAtlas.DefaultCharacters));
    }

    public void Dispose() => _font.Dispose();

    private int Layer(string texture) => _visuals.Blocks.TextureNames.ToList().IndexOf(texture);

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

    /// <summary>
    /// Иконка плиты (шаг 8.6) — нижняя половина кубика: верх опущен на четверть иконки, бока вдвое ниже и берут нижнюю
    /// половину текстуры; низ иконки там же, где у куба.
    /// </summary>
    [Fact]
    public void SlabIcon_IsLowerHalfOfCube()
    {
        const int Scale = 2;
        var s = ItemIcons.Size * Scale;
        ItemIcons.Draw(_canvas, _items.Get("base:stone_slab"), _visuals, 100, 50, Scale);
        var v = _canvas.Vertices.ToArray();

        Assert.Equal(3, _canvas.QuadCount);
        // Верх: дальний угол ромба — на s/4 ниже, чем у куба; ромб того же размера.
        Assert.Equal(new Vector2(100 + s / 2, 50 + s / 4), v[0].Position);
        Assert.Equal(new Vector2(100 + s / 2, 50 + s * 3 / 4), v[2].Position);
        // Южный бок: от верха плиты до низа иконки, высотой s/4, текстура — от середины до низа.
        Assert.Equal(new Vector2(100, 50 + s / 2), v[4].Position);
        Assert.Equal(new Vector2(100 + s / 2, 50 + s), v[6].Position);
        Assert.Equal((0.5f, 1f), (v[4].Uv.Y, v[6].Uv.Y));
        Assert.All(v, x => Assert.Equal(Layer("base:stone"), x.Layer));
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
    public void NotBlockItem_WithoutIcon_IsMissingTextureSquare()
    {
        var items = new ItemRegistry();
        var stick = items.Register("base:stick", 64);
        items.Freeze();

        ItemIcons.Draw(_canvas, stick, new ItemVisuals(_visuals.Blocks, items, new Dictionary<string, string>()), 0, 0, 2);

        Assert.Equal(1, _canvas.QuadCount);
        Assert.All(_canvas.Vertices.ToArray(), v => Assert.Equal(0, v.Layer));
    }

    /// <summary>Своя картинка важнее кубика: и у предмета-не-блока, и у предмета-блока — один плоский квадрат её слоя.</summary>
    [Fact]
    public void ItemWithIcon_IsFlatSquareOfItsLayer_EvenIfItPlacesBlock()
    {
        var items = new ItemRegistry();
        var pickaxe = items.Register("base:pickaxe", 1);
        var stone = items.Register("base:stone", 64, _blocks.Get("base:stone").Id);
        items.Freeze();
        var visuals = new ItemVisuals(_visuals.Blocks, items,
            new Dictionary<string, string> { ["base:pickaxe"] = "base:pickaxe", ["base:stone"] = "base:stone_icon" });

        ItemIcons.Draw(_canvas, pickaxe, visuals, 0, 0, 3);
        ItemIcons.Draw(_canvas, stone, visuals, 100, 0, 3);

        Assert.Equal(2, _canvas.QuadCount);
        var vertices = _canvas.Vertices.ToArray();
        Assert.All(vertices[..4], v => Assert.Equal(_visuals.Blocks.TextureNames.Count, v.Layer));
        Assert.All(vertices[4..], v => Assert.Equal(_visuals.Blocks.TextureNames.Count + 1, v.Layer));
        // Сторона — Size × масштаб, без растяжения: пиксель картинки ложится на целый квадрат 3 × 3.
        Assert.Equal(ItemIcons.Size * 3, vertices[2].Position.X - vertices[0].Position.X);
    }

    [Fact]
    public void SameIcon_SharesOneLayer_AfterBlockLayers()
    {
        var items = new ItemRegistry();
        var a = items.Register("base:a", 1);
        var b = items.Register("base:b", 1);
        var c = items.Register("base:c", 1);
        items.Freeze();

        var visuals = new ItemVisuals(_visuals.Blocks, items,
            new Dictionary<string, string> { ["base:a"] = "base:shared", ["base:b"] = "base:shared" });

        Assert.Equal(["base:shared"], visuals.TextureNames);
        Assert.Equal(_visuals.Blocks.TextureNames.Count, visuals.IconLayer(a.Id));
        Assert.Equal(visuals.IconLayer(a.Id), visuals.IconLayer(b.Id));
        Assert.Equal(ItemVisuals.NoIcon, visuals.IconLayer(c.Id));
    }
}
