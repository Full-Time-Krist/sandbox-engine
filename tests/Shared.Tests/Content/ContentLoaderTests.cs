using Shared.Content;
using Shared.Blocks;
using Shared.Items;

namespace Shared.Tests.Content;

public class ContentLoaderTests
{
    [Fact]
    public void Load_RegistersAirAndBuildsFrozenRegistries()
    {
        var content = ContentLoader.Load([
            File("base/stone.json", """{"type":"block","name":"base:stone","isOpaque":true}"""),
            File("base/stone_item.json", """{"type":"item","name":"base:stone","maxStackSize":64,"placesBlock":"base:stone"}""")
        ]);

        Assert.True(content.Blocks.IsFrozen);
        Assert.True(content.Items.IsFrozen);
        Assert.Equal(BlockId.Air, content.Blocks.Get(BlockRegistry.AirName).Id);
        Assert.Equal(ItemId.Air, content.Items.Get(ItemRegistry.AirName).Id);
        Assert.True(content.Blocks.Get("base:stone").IsOpaque);
        Assert.True(content.Blocks.Get("base:stone").IsSolid); // значение по умолчанию
        Assert.Equal(content.Blocks.Get("base:stone").Id, content.Items.Get("base:stone").Block);
    }

    [Fact]
    public void Load_AssignsNumericIdsIndependentlyOfFileOrder()
    {
        var first = ContentLoader.Load([
            File("base/z.json", """{"type":"block","name":"base:stone","isOpaque":true}"""),
            File("base/a.json", """{"type":"block","name":"base:dirt","isOpaque":true}""")
        ]);
        var second = ContentLoader.Load([
            File("base/a.json", """{"type":"block","name":"base:dirt","isOpaque":true}"""),
            File("base/z.json", """{"type":"block","name":"base:stone","isOpaque":true}""")
        ]);

        Assert.Equal(first.Blocks.Get("base:dirt").Id, second.Blocks.Get("base:dirt").Id);
        Assert.Equal(first.Blocks.Get("base:stone").Id, second.Blocks.Get("base:stone").Id);
        Assert.Equal(new BlockId(1), first.Blocks.Get("base:dirt").Id);
        Assert.Equal(new BlockId(2), first.Blocks.Get("base:stone").Id);
    }

    [Fact]
    public void Load_RegistersNonBlockItem()
    {
        var content = ContentLoader.Load([
            File("base/stick.json", """{"type":"item","name":"base:stick","maxStackSize":16}""")
        ]);

        var stick = content.Items.Get("base:stick");
        Assert.Equal(16, stick.MaxStackSize);
        Assert.Null(stick.Block);
    }

    [Theory]
    [InlineData("""{"type":"block","name":"stone","isOpaque":true}""", "base/stone.json")]
    [InlineData("""{"type":"block","name":"base:stone"}""", "base/stone.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":0}""", "base/stick.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":64,"placesBlock":"base:missing"}""", "base/stick.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"typo":false}""", "base/stone.json")]
    [InlineData("""{"type":"entity","name":"base:ghost"}""", "base/ghost.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":"64"}""", "base/stick.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"hardness":-1}""", "base/stone.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"hardness":"hard"}""", "base/stone.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"toolTier":1}""", "base/stone.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"tool":"Pick Axe"}""", "base/stone.json")]
    [InlineData("""{"type":"item","name":"base:pick","maxStackSize":1,"tool":{"type":"pickaxe","tier":1}}""", "base/pick.json")]
    [InlineData("""{"type":"item","name":"base:pick","maxStackSize":1,"tool":{"type":"pickaxe","speed":0}}""", "base/pick.json")]
    [InlineData("""{"type":"item","name":"base:pick","maxStackSize":1,"tool":{"type":"pickaxe","speed":2,"power":9}}""", "base/pick.json")]
    [InlineData("""{"type":"item","name":"base:pick","maxStackSize":1,"tool":"pickaxe"}""", "base/pick.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":64,"behavior":"wand"}""", "base/stick.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":64,"behavior":42}""", "base/stick.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"textures":"../secret"}""", "base/stone.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"textures":42}""", "base/stone.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"textures":{"top":"a","side":"b"}}""", "base/stone.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"textures":{"top":"a","side":"b","bottom":"c","front":"d"}}""", "base/stone.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":64.5}""", "base/stick.json")]
    public void Load_InvalidContent_ThrowsUsefulError(string json, string path)
    {
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([File(path, json)]));

        Assert.Contains(path, exception.Message);
    }

    [Fact]
    public void Load_DuplicateBlockNames_Throws()
    {
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File("base/one.json", """{"type":"block","name":"base:stone","isOpaque":true}"""),
            File("base/two.json", """{"type":"block","name":"base:stone","isOpaque":false}""")
        ]));

        Assert.Contains("base:stone", exception.Message);
        Assert.Contains("two.json", exception.Message);
    }

    [Fact]
    public void Load_DuplicateItemNames_Throws()
    {
        Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File("base/one.json", """{"type":"item","name":"base:stick","maxStackSize":64}"""),
            File("base/two.json", """{"type":"item","name":"base:stick","maxStackSize":16}""")
        ]));
    }

    [Fact]
    public void Load_MalformedJson_ReportsPath()
    {
        var exception = Assert.Throws<ContentLoadException>(() =>
            ContentLoader.Load([File("base/broken.json", """{"type":"block", """)]));

        Assert.Contains("broken.json", exception.Message);
    }

    [Fact]
    public void Load_RejectsAirAsPlacedBlock()
    {
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File("base/air_item.json", """{"type":"item","name":"base:empty","maxStackSize":1,"placesBlock":"base:air"}""")
        ]));

        Assert.Contains("base:air", exception.Message);
    }

    [Fact]
    public void Load_DuplicateProperty_NamesThatProperty()
    {
        // Без ранней проверки читалось последнее "type", и ошибка ругалась на "isOpaque".
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File("base/stone.json", """{"type":"block","name":"base:stone","isOpaque":true,"type":"item"}""")
        ]));

        Assert.Contains("'type'", exception.Message);
        Assert.Contains("несколько раз", exception.Message);
    }

    [Fact]
    public void Load_Textures_StringIsUniform_ObjectIsPerFace_MissingIsAbsent()
    {
        var content = ContentLoader.Load([
            File("base/stone.json", """{"type":"block","name":"base:stone","isOpaque":true,"textures":"stone"}"""),
            File("base/grass.json", """{"type":"block","name":"base:grass","isOpaque":true,"textures":{"top":"grass_top","side":"grass_side","bottom":"dirt"}}"""),
            File("base/glass.json", """{"type":"block","name":"base:glass","isOpaque":false}""")
        ]);

        Assert.Equal(new BlockTextures("base:stone", "base:stone", "base:stone"), content.BlockTextures["base:stone"]);
        Assert.Equal(new BlockTextures("base:grass_top", "base:grass_side", "base:dirt"), content.BlockTextures["base:grass"]);
        Assert.False(content.BlockTextures.ContainsKey("base:glass"));
    }

    [Fact]
    public void Load_ItemBehavior_ExplicitOrDefault()
    {
        var content = ContentLoader.Load([
            File("base/stone.json", """{"type":"block","name":"base:stone","isOpaque":true}"""),
            File("base/stone_item.json", """{"type":"item","name":"base:stone","maxStackSize":64,"placesBlock":"base:stone"}"""),
            File("base/wand.json", """{"type":"item","name":"base:wand","maxStackSize":1,"behavior":"base:wand"}""")
        ]);

        Assert.Equal(ItemRegistry.PlaceBlockBehavior, content.Items.Get("base:stone").Behavior);
        Assert.Equal("base:wand", content.Items.Get("base:wand").Behavior);
    }

    [Fact]
    public void Load_ShortTextureName_GetsOwnPack_FullNameIsKept()
    {
        var content = ContentLoader.Load([
            File("example_mod/ore.json",
                """{"type":"block","name":"example_mod:ore","isOpaque":true,"textures":{"top":"ore","side":"base:stone","bottom":"base:stone"}}""")
        ]);

        Assert.Equal(new BlockTextures("example_mod:ore", "base:stone", "base:stone"), content.BlockTextures["example_mod:ore"]);
    }

    [Fact]
    public void Load_Icon_ShortNameGetsOwnPack_InvalidIsError()
    {
        var content = ContentLoader.Load([
            File("base/pickaxe.json", """{"type":"item","name":"base:pickaxe","maxStackSize":1,"icon":"pickaxe"}"""),
            File("base/rock.json", """{"type":"item","name":"base:rock","maxStackSize":64,"icon":"other_mod:rock"}"""),
            File("base/stick.json", """{"type":"item","name":"base:stick","maxStackSize":64}""")
        ]);

        Assert.Equal("base:pickaxe", content.ItemIcons["base:pickaxe"]);
        Assert.Equal("other_mod:rock", content.ItemIcons["base:rock"]);
        Assert.False(content.ItemIcons.ContainsKey("base:stick"));
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File("base/bad.json", """{"type":"item","name":"base:bad","maxStackSize":1,"icon":"../bad"}""")]));
        Assert.Contains("'icon'", exception.Message);
    }

    [Fact]
    public void Load_MiningAndTool_FieldsAndDefaults()
    {
        var content = ContentLoader.Load([
            File("base/stone.json", """{"type":"block","name":"base:stone","isOpaque":true,"hardness":2.25,"tool":"pickaxe","toolTier":1}"""),
            File("base/glass.json", """{"type":"block","name":"base:glass","isOpaque":false}"""),
            File("base/pick.json", """{"type":"item","name":"base:pick","maxStackSize":1,"tool":{"type":"pickaxe","tier":2,"speed":4}}"""),
            File("base/rock.json", """{"type":"item","name":"base:rock","maxStackSize":64}""")
        ]);

        Assert.Equal(new BlockMining(2.25f, "pickaxe", 1), content.Blocks.Get("base:stone").Mining);
        Assert.Equal(BlockMining.Default, content.Blocks.Get("base:glass").Mining);
        Assert.Equal(new ToolInfo("pickaxe", 2, 4), content.Items.Get("base:pick").Tool);
        Assert.Null(content.Items.Get("base:rock").Tool);
        Assert.Equal(ItemRegistry.HandBehavior, content.Items.Get("base:pick").Behavior); // инструмент ломает как рука, только быстрее
    }

    [Fact]
    public void Load_DisplayName_IsOptional()
    {
        var content = ContentLoader.Load([
            File("base/stick.json", """{"type":"item","name":"base:stick","maxStackSize":64,"displayName":"Палка"}"""),
            File("base/rock.json", """{"type":"item","name":"base:rock","maxStackSize":64}""")
        ]);

        Assert.Equal("Палка", content.ItemDisplayNames["base:stick"]);
        Assert.False(content.ItemDisplayNames.ContainsKey("base:rock"));
    }

    [Fact]
    public void LoadDirectory_ReadsNestedFiles_AndReportsRelativePath()
    {
        using var directory = new TempContentDirectory();
        directory.Write("base/blocks/stone.json", """{"type":"block","name":"base:stone","isOpaque":true}""");
        directory.Write("base/items/stone.json", """{"type":"item","name":"base:stone","maxStackSize":64,"placesBlock":"base:stone"}""");
        directory.Write("base/items/bad.json", """{"type":"item","name":"base:bad"}""");

        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadDirectory(directory.Path));

        Assert.Contains("base/items/bad.json", exception.Message); // разделитель — '/' на любой ОС
        System.IO.File.Delete(System.IO.Path.Combine(directory.Path, "base", "items", "bad.json"));
        Assert.Equal("base:stone", ContentLoader.LoadDirectory(directory.Path).Items.Get("base:stone").Name);
    }

    [Theory]
    [InlineData("stone.json", "папке пакета")]
    [InlineData("Base/stone.json", "имя пакета 'Base'")]
    [InlineData("my-pack/stone.json", "имя пакета 'my-pack'")]
    [InlineData("other/stone.json", "не из пространства имён пакета 'other'")]
    public void Load_DefinitionOutsideItsPackNamespace_Throws(string path, string expected)
    {
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File(path, """{"type":"block","name":"base:stone","isOpaque":true}""")
        ]));

        Assert.Contains(path, exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Load_ItemMayPlaceBlockOfAnotherPack()
    {
        var content = ContentLoader.Load([
            File("base/stone.json", """{"type":"block","name":"base:stone","isOpaque":true}"""),
            File("example_mod/stone_brush.json", """{"type":"item","name":"example_mod:stone_brush","maxStackSize":1,"placesBlock":"base:stone"}""")
        ]);

        Assert.Equal(content.Blocks.Get("base:stone").Id, content.Items.Get("example_mod:stone_brush").Block);
    }

    [Fact]
    public void LoadDirectories_MergesPacksFromSeveralRoots()
    {
        using var game = new TempContentDirectory();
        using var mods = new TempContentDirectory();
        game.Write("base/blocks/stone.json", """{"type":"block","name":"base:stone","isOpaque":true}""");
        mods.Write("example_mod/items/stone_brush.json",
            """{"type":"item","name":"example_mod:stone_brush","maxStackSize":1,"placesBlock":"base:stone"}""");

        var content = ContentLoader.LoadDirectories([game.Path, mods.Path]);

        Assert.Equal(content.Blocks.Get("base:stone").Id, content.Items.Get("example_mod:stone_brush").Block);
    }

    [Fact]
    public void LoadDirectories_SamePackInTwoRoots_Throws()
    {
        using var game = new TempContentDirectory();
        using var mods = new TempContentDirectory();
        game.Write("base/blocks/stone.json", """{"type":"block","name":"base:stone","isOpaque":true}""");
        mods.Write("base/blocks/dirt.json", """{"type":"block","name":"base:dirt","isOpaque":true}""");

        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadDirectories([game.Path, mods.Path]));

        Assert.Contains("'base'", exception.Message);
        Assert.Contains(game.Path, exception.Message);
        Assert.Contains(mods.Path, exception.Message);
    }

    [Fact]
    public void LoadDirectory_JsonOutsidePack_Throws()
    {
        using var directory = new TempContentDirectory();
        directory.Write("base/blocks/stone.json", """{"type":"block","name":"base:stone","isOpaque":true}""");
        directory.Write("stray.json", """{"type":"block","name":"base:dirt","isOpaque":true}""");

        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadDirectory(directory.Path));

        Assert.Contains("stray.json", exception.Message);
    }

    // --- Замена определений ---

    private static readonly ContentJsonFile BaseStone = File("base/stone.json", """{"type":"block","name":"base:stone","isOpaque":true,"textures":"stone"}""");
    private static readonly ContentJsonFile BaseStoneItem =
        File("base/stone_item.json", """{"type":"item","name":"base:stone","maxStackSize":64,"placesBlock":"base:stone"}""");
    private static readonly ContentJsonFile BaseDirt = File("base/dirt.json", """{"type":"block","name":"base:dirt","isOpaque":true}""");

    private static ContentPack Pack(string name, params string[] dependencies) => new(name, "", dependencies);

    [Fact]
    public void Override_ReplacesWholeDefinition_IdsUnchanged()
    {
        ContentJsonFile[] files = [BaseStone, BaseStoneItem, BaseDirt];
        var plain = ContentLoader.Load(files, [Pack("base")]);

        var content = ContentLoader.Load([.. files,
            File("mod_a/stone.json", """{"type":"block","name":"base:stone","isOpaque":false,"override":true,"textures":"shiny"}"""),
            File("mod_a/stone_item.json", """{"type":"item","name":"base:stone","maxStackSize":16,"placesBlock":"base:stone","override":true}""")
        ], [Pack("base"), Pack("mod_a", "base")]);

        Assert.False(content.Blocks.Get("base:stone").IsOpaque);
        Assert.Equal(new BlockTextures("mod_a:shiny", "mod_a:shiny", "mod_a:shiny"), content.BlockTextures["base:stone"]);
        Assert.Equal(16, content.Items.Get("base:stone").MaxStackSize);
        Assert.Equal(plain.Blocks.All.Select(b => b.Name), content.Blocks.All.Select(b => b.Name));
        Assert.Equal(plain.Blocks.Get("base:stone").Id, content.Blocks.Get("base:stone").Id);
    }

    [Theory]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true}""", "\"override\": true")]                // без флага
    [InlineData("""{"type":"block","name":"mod_a:ore","isOpaque":true,"override":true}""", "из своего пакета")]   // своё имя
    [InlineData("""{"type":"block","name":"base:missing","isOpaque":true,"override":true}""", "такого блока нет")]
    [InlineData("""{"type":"item","name":"base:dirt","maxStackSize":1,"override":true}""", "такого предмета нет")] // блок есть, предмета нет
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"override":"yes"}""", "логическим")]
    public void Override_Invalid_Throws(string json, string expected)
    {
        var exception = Assert.Throws<ContentLoadException>(() =>
            ContentLoader.Load([BaseStone, BaseDirt, File("mod_a/x.json", json)], [Pack("base"), Pack("mod_a", "base")]));

        Assert.Contains("mod_a/x.json", exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Override_WithoutDependency_Throws_PointingToModJson()
    {
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([BaseStone,
            File("mod_a/stone.json", """{"type":"block","name":"base:stone","isOpaque":true,"override":true}""")
        ], [Pack("base"), Pack("mod_a")]));

        Assert.Contains("не зависит от пакета 'base'", exception.Message);
        Assert.Contains("mod.json", exception.Message);
    }

    [Fact]
    public void TwoOverrides_LaterDependentWins_IndependentAreAConflict()
    {
        var a = File("mod_a/stone.json", """{"type":"block","name":"base:stone","isOpaque":true,"override":true,"textures":"a"}""");
        var b = File("mod_b/stone.json", """{"type":"block","name":"base:stone","isOpaque":true,"override":true,"textures":"b"}""");

        // mod_b зависит от mod_a: его замена применяется позже и побеждает, в каком бы порядке ни шли файлы.
        var chained = ContentLoader.Load([b, a, BaseStone], [Pack("base"), Pack("mod_a", "base"), Pack("mod_b", "base", "mod_a")]);
        Assert.Equal("mod_b:b", chained.BlockTextures["base:stone"].Top);

        var exception = Assert.Throws<ContentLoadException>(() =>
            ContentLoader.Load([a, b, BaseStone], [Pack("base"), Pack("mod_a", "base"), Pack("mod_b", "base")]));
        Assert.Contains("'mod_a' и 'mod_b'", exception.Message);
        Assert.Contains("не зависящие друг от друга", exception.Message);
    }

    // --- Рецепты ---

    private static readonly ContentJsonFile BaseLog = File("base/log.json", """{"type":"item","name":"base:log","maxStackSize":64}""");
    private static readonly ContentJsonFile BasePlanks = File("base/planks.json", """{"type":"item","name":"base:planks","maxStackSize":64}""");
    private static readonly ContentJsonFile BaseTable = File("base/table.json", """{"type":"block","name":"base:table","isOpaque":true}""");

    private static ContentJsonFile RecipeFile(string path, string body) => File(path, $$"""{"type":"recipe",{{body}}}""");

    [Fact]
    public void Load_Recipe_ResolvesItemsAndStation_CountDefaultsToOne()
    {
        var content = ContentLoader.Load([BaseLog, BasePlanks, BaseTable, BaseStone, BaseStoneItem,
            RecipeFile("base/recipes/planks.json", """
                "name":"base:planks","result":{"item":"base:planks","count":4},"ingredients":[{"item":"base:log"}]
                """),
            RecipeFile("base/recipes/wall.json", """
                "name":"base:wall","result":{"item":"base:stone"},
                "ingredients":[{"item":"base:planks","count":2},{"item":"base:log","count":3}],"station":"base:table"
                """)]);

        var planks = content.Recipes.Get("base:planks");
        Assert.Equal(new ItemStack(content.Items.Get("base:planks").Id, 4), planks.Result);
        Assert.Equal([new ItemStack(content.Items.Get("base:log").Id, 1)], planks.Ingredients);
        Assert.Null(planks.Station);

        var wall = content.Recipes.Get("base:wall");
        Assert.Equal(new ItemStack(content.Items.Get("base:stone").Id, 1), wall.Result);
        Assert.Equal([new ItemStack(content.Items.Get("base:planks").Id, 2), new ItemStack(content.Items.Get("base:log").Id, 3)],
            wall.Ingredients);
        Assert.Equal(content.Blocks.Get("base:table").Id, wall.Station);
        Assert.Equal(["base:planks", "base:wall"], content.Recipes.All.Select(r => r.Name));
    }

    [Theory]
    [InlineData(""" "name":"base:r","ingredients":[{"item":"base:log"}] """, "'result'")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"} """, "'ingredients'")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"},"ingredients":[] """, "'ingredients'")]
    [InlineData(""" "name":"base:r","result":{"item":"base:missing"},"ingredients":[{"item":"base:log"}] """, "неизвестный предмет")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"},"ingredients":[{"item":"base:missing"}] """, "'base:missing'")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"},"ingredients":[{"item":"base:air"}] """, "'base:air'")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks","count":0},"ingredients":[{"item":"base:log"}] """, "положительным")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks","count":65},"ingredients":[{"item":"base:log"}] """, "больше стопки")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"},"ingredients":[{"item":"base:log","count":-2}] """, "положительным")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"},"ingredients":[{"item":"base:log"},{"item":"base:log"}] """, "несколько раз")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"},"ingredients":[{"item":"base:log","amount":2}] """, "ingredients[0].amount")]
    [InlineData(""" "name":"base:r","result":"base:planks","ingredients":[{"item":"base:log"}] """, "'result'")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"},"ingredients":[{"item":"base:log"}],"station":"base:log" """, "не блок")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"},"ingredients":[{"item":"base:log"}],"station":"table" """, "station")]
    [InlineData(""" "name":"base:r","result":{"item":"base:planks"},"ingredients":[{"item":"base:log"}],"time":5 """, "'time'")]
    public void Load_InvalidRecipe_ThrowsUsefulError(string body, string expected)
    {
        var exception = Assert.Throws<ContentLoadException>(() =>
            ContentLoader.Load([BaseLog, BasePlanks, BaseTable, RecipeFile("base/recipes/r.json", body)]));

        Assert.Contains("base/recipes/r.json", exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Load_DuplicateRecipeNames_Throws_ButRecipeMayShareNameWithItem()
    {
        var recipe = """ "name":"base:planks","result":{"item":"base:planks"},"ingredients":[{"item":"base:log"}] """;
        Assert.Throws<ContentLoadException>(() => ContentLoader.Load([BaseLog, BasePlanks,
            RecipeFile("base/a.json", recipe), RecipeFile("base/b.json", recipe)]));

        // Имена рецептов — своё пространство: рецепт base:planks и предмет base:planks не мешают друг другу.
        Assert.Single(ContentLoader.Load([BaseLog, BasePlanks, RecipeFile("base/a.json", recipe)]).Recipes.All);
    }

    [Fact]
    public void Override_ReplacesRecipe_AndChecksItsReferences()
    {
        ContentJsonFile[] files = [BaseLog, BasePlanks, RecipeFile("base/planks.json", """
            "name":"base:planks","result":{"item":"base:planks","count":4},"ingredients":[{"item":"base:log"}]
            """)];

        var content = ContentLoader.Load([.. files, RecipeFile("mod_a/planks.json", """
            "name":"base:planks","override":true,"result":{"item":"base:planks","count":2},"ingredients":[{"item":"base:log"}]
            """)], [Pack("base"), Pack("mod_a", "base")]);
        Assert.Equal(2, Assert.Single(content.Recipes.All).Result.Count);

        // Замена проверяется так же, как обычный рецепт.
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([.. files, RecipeFile("mod_a/planks.json", """
            "name":"base:planks","override":true,"result":{"item":"mod_a:missing"},"ingredients":[{"item":"base:log"}]
            """)], [Pack("base"), Pack("mod_a", "base")]));
        Assert.Contains("mod_a/planks.json", exception.Message);
    }

    private sealed class TempContentDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("content-test-").FullName;

        public void Write(string relativePath, string json)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, json);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static ContentJsonFile File(string path, string json) => new(path, json);
}
