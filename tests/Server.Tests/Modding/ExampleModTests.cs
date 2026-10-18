using System.Numerics;
using System.Runtime.Loader;
using Api;
using Server.Modding;
using Shared;
using Shared.Content;
using Shared.Items;
using Shared.Modding;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests.Modding;

/// <summary>Пример мода из <c>examples/ExampleMod</c>, загруженный из своей папки так же, как его загружает игра.</summary>
public sealed class ExampleModTests : IDisposable
{
    private static readonly Vector3 Down = new(0, -1, 0);

    private readonly List<string> _log = [];
    private readonly List<string> _tempDirectories = [];
    private GameServer? _server;

    public void Dispose()
    {
        _server?.Dispose();
        foreach (var directory in _tempDirectories)
            Directory.Delete(directory, recursive: true);
    }

    /// <summary>Папка собранного мода: examples/ExampleMod/bin/&lt;конфигурация тестов&gt;/example_mod.</summary>
    internal static string ExampleModDirectory()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = output.Parent!.Name; // …/bin/Debug/net10.0
        var root = output;
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Game.slnx")))
            root = root.Parent;
        return Path.Combine(root!.FullName, "examples", "ExampleMod", "bin", configuration, "example_mod");
    }

    private static ModInfo ExampleMod() => ModDiscovery.ReadManifest(ExampleModDirectory());

    private string TempMod(string id, string manifestExtra = "")
    {
        var directory = Path.Combine(Directory.CreateTempSubdirectory("mod-test-").FullName, id);
        _tempDirectories.Add(Path.GetDirectoryName(directory)!);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ModDiscovery.ManifestFile),
            $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0"{{manifestExtra}}}""");
        return directory;
    }

    [Fact]
    public void Content_OfMod_IsLoadedAsItsPack()
    {
        var content = GameContent.Load([ExampleMod()]);

        Assert.True(content.Blocks.TryGet("example_mod:amethyst_ore", out _));
        Assert.Equal("example_mod:wand", content.Items.Get("example_mod:wand").Behavior);
        Assert.Equal(new BlockTextures("example_mod:amethyst_ore", "example_mod:amethyst_ore", "example_mod:amethyst_ore"),
            content.BlockTextures["example_mod:amethyst_ore"]);
        Assert.Equal("Волшебная палочка", content.ItemDisplayNames["example_mod:wand"]);
        Assert.True(File.Exists(Path.Combine(ExampleMod().BlockTextureDirectory, "amethyst_ore.png")));
        // Картинка предмета — из textures/items мода.
        Assert.Equal("example_mod:wand", content.ItemIcons["example_mod:wand"]);
        Assert.True(File.Exists(Path.Combine(ExampleMod().ItemTextureDirectory, "wand.png")));
        // Замена ванильного определения: песок с текстурой мода.
        Assert.Equal("example_mod:sand_rippled", content.BlockTextures["base:sand"].Top);
        // Рецепт мода смешивает свои и ванильные предметы и ванильный станок.
        var wandRecipe = content.Recipes.Get("example_mod:wand");
        Assert.Equal(content.Items.Get("example_mod:wand").Id, wandRecipe.Result.Item);
        Assert.Contains(new ItemStack(content.Items.Get("base:stick").Id, 1), wandRecipe.Ingredients);
        Assert.Equal(content.Blocks.Get("base:crafting_table").Id, wandRecipe.Station);
        Assert.True(File.Exists(Path.Combine(ExampleMod().BlockTextureDirectory, "sand_rippled.png")));
    }

    [Fact]
    public void Assembly_IsLoadedInItsOwnContext_WithGameApi()
    {
        var mod = ModAssemblies.CreateMod(ExampleMod());

        Assert.NotNull(mod);
        var context = AssemblyLoadContext.GetLoadContext(mod.GetType().Assembly);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.Equal("mod:example_mod", context!.Name);
        // API — из игры, а не своя копия: иначе приведение к IMod выше не сработало бы, а здесь сборки различались бы.
        Assert.Same(typeof(IMod).Assembly, mod.GetType().GetInterface(nameof(IMod))!.Assembly);
    }

    private (TestServers.TestClient Client, Inventory Inventory) StartWithExampleMod()
    {
        _server = TestServers.Create(modInfos: [ExampleMod()], modLog: _log.Add);
        var client = TestServers.JoinAndLoad(_server);
        return (client, _server.PlayerInventory(client.PlayerId));
    }

    [Fact]
    public void PlayerJoined_GivesWand_AndLogsWithModId()
    {
        var (_, inventory) = StartWithExampleMod();
        var wand = GameContent.Load([ExampleMod()]).Items.Get("example_mod:wand").Id;

        Assert.Equal(new ItemStack(wand, 1), inventory[0]);
        Assert.Contains("[Mod example_mod] Tester получил волшебную палочку", _log);
    }

    [Fact]
    public void Wand_TurnsStoneIntoOre_BreakingOreGivesBonus()
    {
        var (client, inventory) = StartWithExampleMod();
        var content = GameContent.Load([ExampleMod()]);
        var surface = TestServers.TopBlock(_server!.World, 0, 0);
        _server.World.TrySetBlock(surface, content.Blocks.Get("base:stone").Id);
        var feet = new Vector3d(0.5, surface.Y + 2, 0.5);
        var eye = feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0);

        // Палочка в первом слоте (её выдал мод при входе): ЛКМ по камню.
        TestServers.Teleport(_server, client, feet);
        client.Send(new UseItem(ItemAction.Primary, eye, Down));
        _server.Tick();
        Assert.Equal(content.Blocks.Get("example_mod:amethyst_ore").Id, _server.World.Chunks.GetBlock(surface));

        // Пустой рукой (слот 2) ломаем руду: одну штуку даёт игра, вторую — обработчик мода.
        client.Send(new SelectHotbarSlot(1));
        client.Send(new UseItem(ItemAction.Primary, eye, Down));
        TestServers.Tick(_server, TestServers.HoldBreakTicks); // ломание удержанием: у руды мода твёрдость по умолчанию — 1 с
        Assert.True(_server.World.Chunks.GetBlock(surface).IsAir);
        Assert.Equal(new ItemStack(content.Items.Get("example_mod:amethyst_ore").Id, 2), inventory[1]);
        Assert.Contains("[Mod example_mod] Tester добыл аметистовую руду: +1 в награду", _log);
    }

    [Fact]
    public void DataOnlyMod_HasNoCode_ItsContentLoads()
    {
        var directory = TempMod("data_mod");
        Directory.CreateDirectory(Path.Combine(directory, "content"));
        File.WriteAllText(Path.Combine(directory, "content", "gem.json"), """{"type":"item","name":"data_mod:gem","maxStackSize":16}""");
        var mod = ModDiscovery.ReadManifest(directory);

        Assert.Null(ModAssemblies.CreateMod(mod));
        _server = TestServers.Create(modInfos: [mod]);
        Assert.Equal(16, GameContent.Load([mod]).Items.Get("data_mod:gem").MaxStackSize);
    }

    [Fact]
    public void AssemblyWithoutIMod_OrNotAnAssembly_IsModError()
    {
        var noEntry = TempMod("no_entry", ""","assembly":"NoEntry.dll" """);
        File.Copy(typeof(BlockHit).Assembly.Location, Path.Combine(noEntry, "NoEntry.dll"));
        var garbage = TempMod("garbage", ""","assembly":"Garbage.dll" """);
        File.WriteAllText(Path.Combine(garbage, "Garbage.dll"), "это не сборка");

        var missingEntry = Assert.Throws<ModLoadException>(() => ModAssemblies.CreateMod(ModDiscovery.ReadManifest(noEntry)));
        var notAssembly = Assert.Throws<ModLoadException>(() => ModAssemblies.CreateMod(ModDiscovery.ReadManifest(garbage)));

        Assert.Contains("'no_entry'", missingEntry.Message);
        Assert.Contains("нет класса, реализующего IMod", missingEntry.Message);
        Assert.Contains("'garbage'", notAssembly.Message);
        Assert.Contains("не загрузилась", notAssembly.Message);
    }

    /// <summary>
    /// Мод уменьшил предел стопки камня до 16 заменой определения, а в сохранении у игрока 64: мир открывается,
    /// стопка урезана до нового предела (а не падает на проверке инвентаря).
    /// </summary>
    [Fact]
    public void OverrideLoweringStackSize_SavedStackIsClamped()
    {
        var savePath = Path.Combine(Directory.CreateTempSubdirectory("override-save-").FullName, "world.db");
        _tempDirectories.Add(Path.GetDirectoryName(savePath)!);
        var stone = GameContent.Load().Items.Get("base:stone");
        using (var first = TestServers.Create(savePath: savePath))
        {
            var client = TestServers.JoinAndLoad(first);
            first.PlayerInventory(client.PlayerId).Set(0, new ItemStack(stone.Id, 64));
        }

        var mod = TempMod("small_stacks", ""","dependencies":{"base":"1.0"}""");
        Directory.CreateDirectory(Path.Combine(mod, "content"));
        File.WriteAllText(Path.Combine(mod, "content", "stone.json"),
            """{"type":"item","name":"base:stone","maxStackSize":16,"placesBlock":"base:stone","override":true}""");
        _server = TestServers.Create(savePath: savePath, modInfos: [ModDiscovery.ReadManifest(mod)]);
        var reopened = TestServers.JoinAndLoad(_server);

        Assert.Equal(new ItemStack(stone.Id, 16), _server.PlayerInventory(reopened.PlayerId)[0]);
    }

    private sealed class FailingMod : IMod
    {
        public void Load(IModContext context) => context.RegisterItemBehavior("base:stolen", new WandStub());
    }

    private sealed class WandStub : IItemBehavior
    {
        public void Use(ItemAction action, in ItemUseContext context) { }
    }

    [Fact]
    public void ExceptionInLoad_IsModError_NamingMod()
    {
        var exception = Assert.Throws<ModLoadException>(() => TestServers.Create(mods: [("thief_mod", new FailingMod())]));

        Assert.Contains("Мод 'thief_mod': ошибка в IMod.Load", exception.Message);
        Assert.Contains("base:stolen", exception.Message);
    }
}
