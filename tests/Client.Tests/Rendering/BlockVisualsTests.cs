using Client.Rendering;
using Shared.Blocks;

namespace Client.Tests.Rendering;

public class BlockVisualsTests
{
    private static int FaceIndex(FaceKind kind) => Array.FindIndex(BlockFace.All, f => f.Kind == kind);

    [Fact]
    public void RequiresFrozenRegistry()
    {
        var registry = new BlockRegistry();

        Assert.Throws<InvalidOperationException>(() => new BlockVisuals(registry, BlockVisuals.BaseAppearances));
    }

    [Fact]
    public void Layer0_IsMissingTexture()
    {
        var registry = new BlockRegistry();
        registry.Freeze();

        var visuals = new BlockVisuals(registry, BlockVisuals.BaseAppearances);

        Assert.Equal(BlockVisuals.MissingTexture, visuals.TextureNames[0]);
    }

    [Fact]
    public void Grass_HasDifferentTopSideAndBottom()
    {
        var registry = new BlockRegistry();
        var blocks = BaseBlocks.Register(registry);
        registry.Freeze();

        var visuals = new BlockVisuals(registry, BlockVisuals.BaseAppearances);

        string NameOf(FaceKind kind) => visuals.TextureNames[visuals.GetLayer(blocks.Grass, FaceIndex(kind))];
        Assert.Equal("grass_top", NameOf(FaceKind.Top));
        Assert.Equal("grass_side", NameOf(FaceKind.Side));
        Assert.Equal("dirt", NameOf(FaceKind.Bottom));
    }

    [Fact]
    public void AllSideFaces_ShareOneLayer()
    {
        var registry = new BlockRegistry();
        var blocks = BaseBlocks.Register(registry);
        registry.Freeze();

        var visuals = new BlockVisuals(registry, BlockVisuals.BaseAppearances);

        var sideLayers = Enumerable.Range(0, BlockFace.All.Length)
            .Where(f => BlockFace.All[f].Kind == FaceKind.Side)
            .Select(f => visuals.GetLayer(blocks.Log, f))
            .Distinct();
        Assert.Single(sideLayers);
    }

    [Fact]
    public void SharedTexture_GetsOneLayer()
    {
        var registry = new BlockRegistry();
        var blocks = BaseBlocks.Register(registry);
        registry.Freeze();

        var visuals = new BlockVisuals(registry, BlockVisuals.BaseAppearances);

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

        var visuals = new BlockVisuals(registry, BlockVisuals.BaseAppearances);

        for (var f = 0; f < BlockFace.All.Length; f++)
            Assert.Equal(0, visuals.GetLayer(mystery, f));
    }

    [Fact]
    public void BaseBlocks_AllHaveAppearance()
    {
        // Блок без описания внешнего вида будет в шахматку: тест не даст забыть его при добавлении нового блока.
        var registry = new BlockRegistry();
        BaseBlocks.Register(registry);
        registry.Freeze();

        foreach (var definition in registry.All.Where(d => !d.Id.IsAir))
            Assert.True(BlockVisuals.BaseAppearances.ContainsKey(definition.Name), $"Нет внешнего вида для {definition.Name}");
    }
}
