using System.Numerics;
using Api;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Modding;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

/// <summary>
/// Факел (шаг 8.5) и неполные блоки на сервере (шаг 8.6): факел ставится только поверх твёрдого, светит, ломается с добычей, когда опора перестаёт быть твёрдой —
/// кто бы её ни сменил (игрок, мод через <see cref="IWorld"/>).
/// </summary>
public sealed class TorchTests : IDisposable
{
    private static readonly BlockPos Up = new(0, 1, 0);
    private static readonly BlockPos East = new(1, 0, 0);

    private GameServer _server;
    private TestServers.TestClient _client;
    private readonly BlockRegistry _registry;
    private readonly ItemRegistry _items;
    private readonly BlockId _torch;
    private readonly BlockId _stone;
    private readonly BlockId _planks;
    private string? _modDirectory;

    public TorchTests()
    {
        var content = GameContent.Load();
        _registry = content.Blocks;
        _items = content.Items;
        _torch = _registry.Get("base:torch").Id;
        _stone = _registry.Get("base:stone").Id;
        _planks = _registry.Get("base:planks").Id;
        (_server, _client) = Start(TestServers.Create());
    }

    public void Dispose()
    {
        _server.Dispose();
        if (_modDirectory is not null)
            Directory.Delete(_modDirectory, recursive: true);
    }

    private static (GameServer, TestServers.TestClient) Start(GameServer server)
    {
        var client = TestServers.JoinAndLoad(server);
        var inventory = server.PlayerInventory(client.PlayerId);
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
            inventory.Set(slot, ItemStack.Empty);
        return (server, client);
    }

    private IWorld World => _server.ModWorld;
    private IPlayer Player => _server.ModPlayer(_client.PlayerId);
    private BlockId BlockAt(BlockPos pos) => _server.World.Chunks.GetBlock(pos);
    private ItemId TorchItem => _items.ForBlock(_torch);

    private int BlockLightAt(BlockPos pos) => _server.World.Chunks.Get(pos.Chunk)!.Light.Get(pos.Local.Index) & 0x0F;

    /// <summary>
    /// Пустое место для опытов: над верхним блоком столбца (0, 0), куб 3 × 4 × 3 воздуха, в его нижнем центре — камень.
    /// Возвращает камень. Рядом может быть рельеф, поэтому место расчищается явно.
    /// </summary>
    private BlockPos StoneInClearing()
    {
        var center = TestServers.TopBlock(_server.World, 0, 0) + new BlockPos(0, 4, 0);
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 2; dy++)
        for (var dz = -1; dz <= 1; dz++)
            Assert.True(World.TrySetBlock(center + new BlockPos(dx, dy, dz), BlockId.Air));
        Assert.True(World.TrySetBlock(center, _stone));
        return center;
    }

    /// <summary>Ставит факел, как игрок: поверх верхнего блока столбца (0, 0), глядя вниз сверху, факел в слоте 0.</summary>
    [Fact]
    public void Player_PlacesTorch_OnTopOfSolid_ItLights()
    {
        var surface = TestServers.TopBlock(_server.World, 0, 0);
        Assert.True(_registry.IsSolid(BlockAt(surface)));
        _server.PlayerInventory(_client.PlayerId).Set(0, new ItemStack(TorchItem, 5));
        var feet = new Vector3d(0.5, surface.Y + 2, 0.5);
        TestServers.Teleport(_server, _client, feet);

        _client.Send(new UseItem(ItemAction.Secondary, feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), -Vector3.UnitY));
        _server.Tick();

        Assert.Equal(_torch, BlockAt(surface + Up));
        Assert.Equal(new ItemStack(TorchItem, 4), _server.PlayerInventory(_client.PlayerId)[0]);
        Assert.Equal(15, BlockLightAt(surface + Up));
        Assert.Equal(14, BlockLightAt(surface + Up + Up));
    }

    [Fact]
    public void Torch_IsPlacedOnlyOnTopOfSolid()
    {
        var stone = StoneInClearing();

        Assert.False(World.TryPlaceBlock(stone + East, _torch, Player)); // сбоку: под ним воздух
        Assert.True(BlockAt(stone + East).IsAir);
        Assert.True(World.TryPlaceBlock(stone + Up, _torch, Player));
        Assert.False(World.TryPlaceBlock(stone + Up + Up, _torch, Player)); // на факел: он не твёрдый
        Assert.True(BlockAt(stone + Up + Up).IsAir);
        // На плиту — тоже нет: она твёрдая, но верх у неё на половине клетки, и факел висел бы над ней.
        Assert.True(World.TrySetBlock(stone + East + East, _registry.Get("base:stone_slab").Id));
        Assert.True(World.TrySetBlock(stone + East + East + Up, BlockId.Air));
        Assert.False(World.TryPlaceBlock(stone + East + East + Up, _torch, Player));
        // Обычный блок опоры не требует.
        Assert.True(World.TryPlaceBlock(stone + East, _planks, Player));
    }

    [Fact]
    public void SupportRemoved_TorchBreaksWithDrop_AndItsLightGoes()
    {
        var stone = StoneInClearing();
        Assert.True(World.TryPlaceBlock(stone + Up, _torch, Player));
        Assert.Equal(15, BlockLightAt(stone + Up));
        while (_client.TryReceive(out _)) { }

        Assert.True(World.TrySetBlock(stone, BlockId.Air));

        Assert.True(BlockAt(stone + Up).IsAir);
        Assert.Equal(1, TestServers.OnGround(_server, TorchItem));
        Assert.Equal(0, BlockLightAt(stone + Up));
        // Клиент узнал и об опоре, и о факеле — в том же действии.
        var changes = new List<BlockChanged>();
        while (_client.TryReceive(out var message))
        {
            if (message is BlockChanged change)
                changes.Add(change);
        }
        Assert.Equal([new BlockChanged(stone, BlockId.Air), new BlockChanged(stone + Up, BlockId.Air)], changes);
    }

    /// <summary>Опору заменили плитой — твёрдой, но без полного верха: факел падает.</summary>
    [Fact]
    public void SupportReplacedBySlab_TorchBreaks()
    {
        var stone = StoneInClearing();
        Assert.True(World.TryPlaceBlock(stone + Up, _torch, Player));

        Assert.True(World.TrySetBlock(stone, _registry.Get("base:stone_slab").Id));

        Assert.True(BlockAt(stone + Up).IsAir);
        Assert.Equal(1, TestServers.OnGround(_server, TorchItem));
    }

    [Fact]
    public void SupportReplacedBySolid_TorchStays()
    {
        var stone = StoneInClearing();
        Assert.True(World.TryPlaceBlock(stone + Up, _torch, Player));

        Assert.True(World.TrySetBlock(stone, _planks));

        Assert.Equal(_torch, BlockAt(stone + Up));
        Assert.Equal(0, TestServers.OnGround(_server, TorchItem));
    }

    /// <summary>Игрок ломает опору (<see cref="IWorld.TryBreakBlock"/>, как и ЛКМ) — факел падает вместе с ней.</summary>
    [Fact]
    public void PlayerBreaksSupport_TorchDrops()
    {
        var stone = StoneInClearing();
        Assert.True(World.TryPlaceBlock(stone + Up, _torch, Player));

        Assert.True(World.TryBreakBlock(stone, Player));

        Assert.True(BlockAt(stone + Up).IsAir);
        Assert.Equal(1, TestServers.OnGround(_server, TorchItem));
    }

    /// <summary>Твёрдость 0: рукой факел ломается за тик удержания и выпадает.</summary>
    [Fact]
    public void Torch_BreaksByHand_WithDrop()
    {
        var surface = TestServers.TopBlock(_server.World, 0, 0);
        Assert.True(World.TryPlaceBlock(surface + Up, _torch, Player));
        var feet = new Vector3d(0.5, surface.Y + 2, 0.5);
        TestServers.Teleport(_server, _client, feet);

        _client.Send(new UseItem(ItemAction.Primary, feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), -Vector3.UnitY));
        TestServers.Tick(_server, 2);

        Assert.True(BlockAt(surface + Up).IsAir);
        // Подобрать её за два тика игрок не успеет: задержка подбора 0,5 с.
        Assert.Equal(1, TestServers.OnGround(_server, TorchItem));
    }

    /// <summary>
    /// «Блок в теле игрока» — по коробам формы (шаг 8.6): плита под ногами парящего над её верхом игрока его не задевает,
    /// куб в той же клетке — задевает; ноги ниже верха плиты — задевает и плита.
    /// </summary>
    [Fact]
    public void IsBlockedByPlayer_UsesShapeBoxes()
    {
        var stone = StoneInClearing();
        var slab = _registry.Get("base:stone_slab").Id;
        var cell = stone + Up;

        TestServers.Teleport(_server, _client, new Vector3d(cell.X + 0.5, cell.Y + 0.5, cell.Z + 0.5));
        Assert.False(World.IsBlockedByPlayer(cell, slab));
        Assert.True(World.IsBlockedByPlayer(cell, _stone));

        TestServers.Teleport(_server, _client, new Vector3d(cell.X + 0.5, cell.Y + 0.4, cell.Z + 0.5));
        Assert.True(World.IsBlockedByPlayer(cell, slab));
    }

    /// <summary>
    /// Сервер целится по форме (шаг 8.6): луч над столбиком факела, но в его клетке, ломает доски за ним, а факел цел.
    /// С кубической клеткой луч попал бы в факел.
    /// </summary>
    [Fact]
    public void Primary_AboveTorchColumn_BreaksBlockBehind()
    {
        var stone = StoneInClearing();
        var torchPos = stone + Up;
        var wall = torchPos + East;
        Assert.True(World.TryPlaceBlock(torchPos, _torch, Player));
        Assert.True(World.TrySetBlock(wall, _planks));
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        // Глаз в клетке к западу от факела, на высоте 0,8 над низом его клетки — выше столбика (10 пикселей).
        var eye = new Vector3d(torchPos.X - 0.5, torchPos.Y + 0.8, torchPos.Z + 0.5);
        TestServers.Teleport(_server, _client, eye - new Vector3d(0, PlayerPhysics.EyeHeight, 0));

        _client.Send(new UseItem(ItemAction.Primary, eye, Vector3.UnitX));
        TestServers.Tick(_server, TestServers.HoldBreakTicks);

        Assert.True(BlockAt(wall).IsAir);
        Assert.Equal(_torch, BlockAt(torchPos));
    }

    /// <summary>
    /// Блок с опорой у мода может быть твёрдым и держать такой же: убрали нижнюю опору — падает весь столбик, снизу вверх,
    /// каждый своей добычей.
    /// </summary>
    [Fact]
    public void ModSolidBlocksWithSupport_WholeColumnFalls()
    {
        _modDirectory = Directory.CreateTempSubdirectory("support-mod-").FullName;
        Directory.CreateDirectory(Path.Combine(_modDirectory, "content"));
        File.WriteAllText(Path.Combine(_modDirectory, "content", "post.json"),
            """{"type":"block","name":"post_mod:post","isOpaque":true,"needsSupport":true}""");
        File.WriteAllText(Path.Combine(_modDirectory, "content", "post_item.json"),
            """{"type":"item","name":"post_mod:post","maxStackSize":64,"placesBlock":"post_mod:post"}""");
        var version = new Version(1, 0);
        var mod = new ModInfo("post_mod", "Столбы", version, new Dictionary<string, Version> { ["base"] = version }, _modDirectory, null);
        _server.Dispose();
        (_server, _client) = Start(TestServers.Create(modInfos: [mod]));
        var content = GameContent.Load([mod]);
        var post = content.Blocks.Get("post_mod:post").Id;
        var stone = StoneInClearing();
        Assert.True(World.TryPlaceBlock(stone + Up, post, Player));
        Assert.True(World.TryPlaceBlock(stone + Up + Up, post, Player));

        Assert.True(World.TrySetBlock(stone, BlockId.Air));

        Assert.True(BlockAt(stone + Up).IsAir);
        Assert.True(BlockAt(stone + Up + Up).IsAir);
        Assert.Equal(2, TestServers.OnGround(_server, content.Items.ForBlock(post)));
    }
}
