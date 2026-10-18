using Client.Rendering;
using Shared.Modding;

namespace Client.Tests.Rendering;

public class TextureFilesTests
{
    [Fact]
    public void PathFor_ModTextures_InModFolder_GameTextures_InAssets()
    {
        var mod = new ModInfo("my_mod", "Мой мод", new Version(1, 0), new Dictionary<string, Version>(), "/mods/my_mod", null);
        var files = new TextureFiles([mod]);

        Assert.Equal(Path.Combine("/mods/my_mod", "textures", "blocks", "ore.png"), files.PathFor("my_mod:ore", TextureKind.Block));
        Assert.Equal(Path.Combine("/mods/my_mod", "textures", "items", "chisel.png"), files.PathFor("my_mod:chisel", TextureKind.Item));
        Assert.Equal(Path.Combine(TextureFiles.GameDirectory(TextureKind.Block), "stone.png"), files.PathFor("base:stone", TextureKind.Block));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Items", "pickaxe.png"),
            files.PathFor("base:pickaxe", TextureKind.Item));
    }
}
