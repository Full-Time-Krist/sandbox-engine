using Shared.Blocks;
using Shared.Content;
using Shared.Modding;

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
            ("base:copper_ore", true, true, Uniform("copper_ore")),
            ("base:dirt", true, true, Uniform("dirt")),
            ("base:grass", true, true, new("base:grass_top", "base:grass_side", "base:dirt")),
            ("base:leaves", true, true, Uniform("leaves")),
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

    /// <summary>Мод заменяет определение пакета, от которого зависит не прямо, а через другой мод.</summary>
    [Fact]
    public void ModPacks_Dependencies_AreTransitive()
    {
        var root = Directory.CreateTempSubdirectory("content-mods-").FullName;
        try
        {
            var middle = Path.Combine(root, "middle_mod");
            var top = Path.Combine(root, "top_mod");
            Directory.CreateDirectory(middle);
            Directory.CreateDirectory(Path.Combine(top, "content"));
            File.WriteAllText(Path.Combine(top, "content", "stone.json"),
                """{"type":"block","name":"base:stone","isOpaque":true,"override":true,"textures":"stone"}""");
            var version = new Version(1, 0);
            ModInfo[] mods =
            [
                new("middle_mod", "m", version, new Dictionary<string, Version> { ["base"] = version }, middle, null),
                new("top_mod", "t", version, new Dictionary<string, Version> { ["middle_mod"] = version }, top, null),
            ];

            var content = GameContent.Load(mods);

            Assert.Equal("top_mod:stone", content.BlockTextures["base:stone"].Top);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
