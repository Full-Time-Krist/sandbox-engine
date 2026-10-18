using Shared.Blocks;
using Shared.Content;

namespace Shared.Tests.Content;

/// <summary>Настоящий контент игры из <c>content/</c> — тот, что грузят сервер и клиент.</summary>
public class GameContentTests
{
    private readonly ContentRegistries _content = GameContent.Load();

    /// <summary>
    /// Блоки, их свойства и текстуры такие же, какими были в коде до переноса в JSON (шаг 4.2):
    /// BaseBlocks, BlockVisuals.BaseAppearances. Новый блок — новая строка здесь.
    /// </summary>
    [Fact]
    public void Blocks_MatchTablesThatWereInCode()
    {
        (string Name, bool Opaque, bool Solid, BlockTextures Textures)[] expected =
        [
            ("base:dirt", true, true, Uniform("dirt")),
            ("base:grass", true, true, new("base:grass_top", "base:grass_side", "base:dirt")),
            ("base:log", true, true, new("base:log_top", "base:log_side", "base:log_top")),
            ("base:planks", true, true, Uniform("planks")),
            ("base:sand", true, true, Uniform("sand")),
            ("base:stone", true, true, Uniform("stone")),
        ];

        var actual = _content.Blocks.All.Where(block => !block.Id.IsAir)
            .Select(block => (block.Name, block.IsOpaque, block.IsSolid, _content.BlockTextures[block.Name]))
            .ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>Каждому блоку — одноимённый предмет, который его ставит и выпадает из него, стопка 64 (как RegisterBlockItems).</summary>
    [Fact]
    public void EveryBlock_HasItemWithSameName_ThatPlacesIt()
    {
        Assert.Equal(_content.Blocks.Count, _content.Items.Count); // воздух есть в обоих
        foreach (var block in _content.Blocks.All.Where(block => !block.Id.IsAir))
        {
            var item = _content.Items.Get(block.Name);
            Assert.Equal(block.Id, item.Block);
            Assert.Equal(64, item.MaxStackSize);
            Assert.Equal(item.Id, _content.Items.ForBlock(block.Id));
        }
    }

    [Fact]
    public void BaseBlocks_FindsDistinctBlocksByName()
    {
        var blocks = BaseBlocks.From(_content.Blocks);

        BlockId[] ids = [blocks.Stone, blocks.Dirt, blocks.Grass, blocks.Sand, blocks.Log, blocks.Planks];
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(BlockId.Air, ids);
        Assert.Equal("base:grass", _content.Blocks.Get(blocks.Grass).Name);
    }

    /// <summary>Сервер и клиент грузят контент отдельно; клиент сверяет имена по порядку id — загрузки должны совпадать.</summary>
    [Fact]
    public void TwoLoads_GiveSameIds()
    {
        var other = GameContent.Load();

        Assert.Equal(_content.Blocks.All.Select(b => b.Name), other.Blocks.All.Select(b => b.Name));
        Assert.Equal(_content.Items.All.Select(i => i.Name), other.Items.All.Select(i => i.Name));
    }

    private static BlockTextures Uniform(string name) => new("base:" + name, "base:" + name, "base:" + name);

    /// <summary>Названия ванильных предметов переехали из кода (ItemNames) в поле displayName.</summary>
    [Fact]
    public void VanillaItems_HaveDisplayNames()
    {
        Assert.Equal("Дёрн", _content.ItemDisplayNames["base:grass"]);
        Assert.All(_content.Items.All.Where(i => !i.Id.IsAir), item => Assert.True(_content.ItemDisplayNames.ContainsKey(item.Name), item.Name));
    }
}
