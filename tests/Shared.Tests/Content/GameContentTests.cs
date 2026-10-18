using Shared.Blocks;
using Shared.Content;
using Shared.Items;
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
            ("base:crafting_table", true, true, new("base:crafting_table_top", "base:crafting_table_side", "base:planks")),
            ("base:dirt", true, true, Uniform("dirt")),
            ("base:furnace", true, true, new("base:furnace_top", "base:furnace_side", "base:furnace_top")),
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
        foreach (var block in _content.Blocks.All.Where(block => !block.Id.IsAir))
        {
            var item = _content.Items.Get(block.Name);
            Assert.Equal(block.Id, item.Block);
            Assert.Equal(64, item.MaxStackSize);
            Assert.Equal(item.Id, _content.Items.ForBlock(block.Id));
        }
    }

    /// <summary>Лестница инструментов: камень — деревянной киркой и выше, медь — каменной и выше; остальное — рукой.</summary>
    [Fact]
    public void VanillaMining_ToolLadder()
    {
        Assert.Equal(new BlockMining(2.25f, "pickaxe", 1), _content.Blocks.Get("base:stone").Mining);
        Assert.Equal(new BlockMining(4.5f, "pickaxe", 2), _content.Blocks.Get("base:copper_ore").Mining);
        foreach (var name in new[] { "base:dirt", "base:grass", "base:sand", "base:log", "base:planks", "base:leaves" })
            Assert.Equal(0, _content.Blocks.Get(name).Mining.ToolTier);

        foreach (var (material, tier, speed) in new[] { ("wooden", 1, 2f), ("stone", 2, 4f), ("copper", 3, 6f) })
        foreach (var kind in new[] { "pickaxe", "axe", "shovel" })
        {
            var tool = _content.Items.Get($"base:{material}_{kind}");
            Assert.Equal(new ToolInfo(kind, tier, speed), tool.Tool);
            Assert.Equal(1, tool.MaxStackSize);
            Assert.Equal($"base:{material}_{kind}", _content.ItemIcons[tool.Name]);
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

    /// <summary>
    /// Путь от голых рук до медной кирки: начиная с пустого инвентаря, добываем из природных блоков то, что позволяет
    /// лучший инструмент из уже сделанного, и делаем всё, на что хватает (станок считается доступным, если он сделан —
    /// его можно поставить). Медная кирка должна получиться, а камень — только после деревянной кирки.
    /// </summary>
    [Fact]
    public void VanillaRecipes_LeadFromBareHandsToCopperPickaxe()
    {
        string[] natural = ["base:grass", "base:dirt", "base:sand", "base:log", "base:leaves", "base:stone", "base:copper_ore"];
        var have = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();

        for (var changed = true; changed;)
        {
            changed = false;
            var tools = have.Select(name => _content.Items.Get(name).Tool).OfType<ToolInfo>().ToArray();
            foreach (var name in natural)
            {
                var block = _content.Blocks.Get(name);
                var harvest = Mining.CanHarvest(block.Mining, null) || tools.Any(tool => Mining.CanHarvest(block.Mining, tool));
                var drop = _content.Items.Get(_content.Items.ForBlock(block.Id)).Name;
                if (harvest && have.Add(drop))
                {
                    order.Add(drop);
                    changed = true;
                }
            }

            foreach (var recipe in _content.Recipes.All)
            {
                var hasIngredients = recipe.Ingredients.All(i => have.Contains(_content.Items.Get(i.Item).Name));
                var hasStation = recipe.Station is not { } station || have.Contains(_content.Items.Get(_content.Items.ForBlock(station)).Name);
                var result = _content.Items.Get(recipe.Result.Item).Name;
                if (hasIngredients && hasStation && have.Add(result))
                {
                    order.Add(result);
                    changed = true;
                }
            }
        }

        Assert.Contains("base:copper_pickaxe", have);
        Assert.True(order.IndexOf("base:wooden_pickaxe") < order.IndexOf("base:stone"), string.Join(", ", order));
        Assert.True(order.IndexOf("base:stone_pickaxe") < order.IndexOf("base:copper_ore"), string.Join(", ", order));
        // Каждый ванильный предмет, кроме природных блоков, делается по рецепту.
        Assert.All(_content.Items.All.Where(i => !i.Id.IsAir), item => Assert.Contains(item.Name, have));
    }

    /// <summary>Ремесло ванили: доски из бревна, инструменты у верстака, слиток в печи, одна штука инструмента за раз.</summary>
    [Fact]
    public void VanillaRecipes_Stations()
    {
        var table = _content.Blocks.Get("base:crafting_table").Id;
        var furnace = _content.Blocks.Get("base:furnace").Id;

        var planks = _content.Recipes.Get("base:planks");
        Assert.Null(planks.Station);
        Assert.Equal(new ItemStack(_content.Items.Get("base:planks").Id, 4), planks.Result);
        Assert.Equal([new ItemStack(_content.Items.Get("base:log").Id, 1)], planks.Ingredients);
        Assert.Equal(furnace, _content.Recipes.Get("base:copper_ingot").Station);
        foreach (var material in new[] { "wooden", "stone", "copper" })
        foreach (var kind in new[] { "pickaxe", "axe", "shovel" })
        {
            var recipe = _content.Recipes.Get($"base:{material}_{kind}");
            Assert.Equal(table, recipe.Station);
            Assert.Equal(new ItemStack(_content.Items.Get($"base:{material}_{kind}").Id, 1), recipe.Result);
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
