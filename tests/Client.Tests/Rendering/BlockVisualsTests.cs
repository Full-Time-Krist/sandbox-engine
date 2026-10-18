using Client.Rendering;
using Shared.Blocks;
using Shared.Content;

namespace Client.Tests.Rendering;

public class BlockVisualsTests
{
    private static int FaceIndex(FaceKind kind) => Array.FindIndex(BlockFace.All, f => f.Kind == kind);

    private static IReadOnlyDictionary<string, BlockAppearance> Appearances =>
        BlockVisuals.AppearancesFrom(GameContent.Load().BlockTextures);

    [Fact]
    public void RequiresFrozenRegistry()
    {
        var registry = new BlockRegistry();

        Assert.Throws<InvalidOperationException>(() => new BlockVisuals(registry, Appearances));
    }

    [Fact]
    public void Layer0_IsMissingTexture()
    {
        var registry = new BlockRegistry();
        registry.Freeze();

        var visuals = new BlockVisuals(registry, Appearances);

        Assert.Equal(BlockVisuals.MissingTexture, visuals.TextureNames[0]);
    }

    [Fact]
    public void Grass_HasDifferentTopSideAndBottom()
    {
        var registry = GameContent.Load().Blocks;
        var blocks = BaseBlocks.From(registry);

        var visuals = new BlockVisuals(registry, Appearances);

        string NameOf(FaceKind kind) => visuals.TextureNames[visuals.GetLayer(blocks.Grass, FaceIndex(kind))];
        Assert.Equal("grass_top", NameOf(FaceKind.Top));
        Assert.Equal("grass_side", NameOf(FaceKind.Side));
        Assert.Equal("dirt", NameOf(FaceKind.Bottom));
    }

    [Fact]
    public void AllSideFaces_ShareOneLayer()
    {
        var registry = GameContent.Load().Blocks;
        var blocks = BaseBlocks.From(registry);

        var visuals = new BlockVisuals(registry, Appearances);

        var sideLayers = Enumerable.Range(0, BlockFace.All.Length)
            .Where(f => BlockFace.All[f].Kind == FaceKind.Side)
            .Select(f => visuals.GetLayer(blocks.Log, f))
            .Distinct();
        Assert.Single(sideLayers);
    }

    [Fact]
    public void SharedTexture_GetsOneLayer()
    {
        var registry = GameContent.Load().Blocks;
        var blocks = BaseBlocks.From(registry);

        var visuals = new BlockVisuals(registry, Appearances);

        // Низ травы и блок земли используют одну текстуру — и один слой.
        Assert.Equal(visuals.GetLayer(blocks.Dirt, 0), visuals.GetLayer(blocks.Grass, FaceIndex(FaceKind.Bottom)));
        Assert.Equal(visuals.TextureNames.Count, visuals.TextureNames.Distinct().Count());
    }

    [Fact]
    public void BlockWithoutAppearance_UsesMissingTexture()
    {
        var registry = new BlockRegistry();
        var mystery = registry.Register("test:mystery", isOpaque: true).Id;
        registry.Freeze();

        var visuals = new BlockVisuals(registry, Appearances);

        for (var f = 0; f < BlockFace.All.Length; f++)
            Assert.Equal(0, visuals.GetLayer(mystery, f));
    }

    [Fact]
    public void ContentTextures_AllHaveFiles()
    {
        // Текстура без файла рисуется шахматкой «missing» без ошибки: тест не даст опечататься в JSON блока.
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Blocks");
        var textures = GameContent.Load().BlockTextures.Values.SelectMany(t => new[] { t.Top, t.Side, t.Bottom }).Distinct();

        Assert.All(textures, name => Assert.True(File.Exists(Path.Combine(directory, name + ".png")), $"Нет файла текстуры '{name}'"));
    }
}
