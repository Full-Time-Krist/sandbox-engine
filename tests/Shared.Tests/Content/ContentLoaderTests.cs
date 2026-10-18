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
            File("stone.json", """{"type":"block","name":"base:stone","isOpaque":true}"""),
            File("stone_item.json", """{"type":"item","name":"base:stone","maxStackSize":64,"placesBlock":"base:stone"}""")
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
            File("z.json", """{"type":"block","name":"base:stone","isOpaque":true}"""),
            File("a.json", """{"type":"block","name":"base:dirt","isOpaque":true}""")
        ]);
        var second = ContentLoader.Load([
            File("a.json", """{"type":"block","name":"base:dirt","isOpaque":true}"""),
            File("z.json", """{"type":"block","name":"base:stone","isOpaque":true}""")
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
            File("stick.json", """{"type":"item","name":"base:stick","maxStackSize":16}""")
        ]);

        var stick = content.Items.Get("base:stick");
        Assert.Equal(16, stick.MaxStackSize);
        Assert.Null(stick.Block);
    }

    [Theory]
    [InlineData("""{"type":"block","name":"stone","isOpaque":true}""", "stone.json")]
    [InlineData("""{"type":"block","name":"base:stone"}""", "stone.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":0}""", "stick.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":64,"placesBlock":"base:missing"}""", "stick.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"typo":false}""", "stone.json")]
    [InlineData("""{"type":"entity","name":"base:ghost"}""", "ghost.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":"64"}""", "stick.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"textures":"../secret"}""", "stone.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"textures":42}""", "stone.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"textures":{"top":"a","side":"b"}}""", "stone.json")]
    [InlineData("""{"type":"block","name":"base:stone","isOpaque":true,"textures":{"top":"a","side":"b","bottom":"c","front":"d"}}""", "stone.json")]
    [InlineData("""{"type":"item","name":"base:stick","maxStackSize":64.5}""", "stick.json")]
    public void Load_InvalidContent_ThrowsUsefulError(string json, string path)
    {
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([File(path, json)]));

        Assert.Contains(path, exception.Message);
    }

    [Fact]
    public void Load_DuplicateBlockNames_Throws()
    {
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File("one.json", """{"type":"block","name":"base:stone","isOpaque":true}"""),
            File("two.json", """{"type":"block","name":"base:stone","isOpaque":false}""")
        ]));

        Assert.Contains("base:stone", exception.Message);
        Assert.Contains("two.json", exception.Message);
    }

    [Fact]
    public void Load_DuplicateItemNames_Throws()
    {
        Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File("one.json", """{"type":"item","name":"base:stick","maxStackSize":64}"""),
            File("two.json", """{"type":"item","name":"base:stick","maxStackSize":16}""")
        ]));
    }

    [Fact]
    public void Load_MalformedJson_ReportsPath()
    {
        var exception = Assert.Throws<ContentLoadException>(() =>
            ContentLoader.Load([File("broken.json", """{"type":"block", """)]));

        Assert.Contains("broken.json", exception.Message);
    }

    [Fact]
    public void Load_RejectsAirAsPlacedBlock()
    {
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File("air_item.json", """{"type":"item","name":"base:empty","maxStackSize":1,"placesBlock":"base:air"}""")
        ]));

        Assert.Contains("base:air", exception.Message);
    }

    [Fact]
    public void Load_DuplicateProperty_NamesThatProperty()
    {
        // Без ранней проверки читалось последнее "type", и ошибка ругалась на "isOpaque".
        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.Load([
            File("stone.json", """{"type":"block","name":"base:stone","isOpaque":true,"type":"item"}""")
        ]));

        Assert.Contains("'type'", exception.Message);
        Assert.Contains("несколько раз", exception.Message);
    }

    [Fact]
    public void Load_Textures_StringIsUniform_ObjectIsPerFace_MissingIsAbsent()
    {
        var content = ContentLoader.Load([
            File("stone.json", """{"type":"block","name":"base:stone","isOpaque":true,"textures":"stone"}"""),
            File("grass.json", """{"type":"block","name":"base:grass","isOpaque":true,"textures":{"top":"grass_top","side":"grass_side","bottom":"dirt"}}"""),
            File("glass.json", """{"type":"block","name":"base:glass","isOpaque":false}""")
        ]);

        Assert.Equal(new BlockTextures("stone", "stone", "stone"), content.BlockTextures["base:stone"]);
        Assert.Equal(new BlockTextures("grass_top", "grass_side", "dirt"), content.BlockTextures["base:grass"]);
        Assert.False(content.BlockTextures.ContainsKey("base:glass"));
    }

    [Fact]
    public void LoadDirectory_ReadsNestedFiles_AndReportsRelativePath()
    {
        using var directory = new TempContentDirectory();
        directory.Write("base/blocks/stone.json", """{"type":"block","name":"base:stone","isOpaque":true}""");
        directory.Write("base/items/stone.json", """{"type":"item","name":"base:stone","maxStackSize":64,"placesBlock":"base:stone"}""");
        directory.Write("base/items/bad.json", """{"type":"item","name":"base:bad"}""");

        var exception = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadDirectory(directory.Path));

        Assert.Contains(System.IO.Path.Combine("base", "items", "bad.json"), exception.Message);
        System.IO.File.Delete(System.IO.Path.Combine(directory.Path, "base", "items", "bad.json"));
        Assert.Equal("base:stone", ContentLoader.LoadDirectory(directory.Path).Items.Get("base:stone").Name);
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
