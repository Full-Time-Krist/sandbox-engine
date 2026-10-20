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
/// Факел (шаг 8.5): ставится только поверх твёрдого, светит, ломается с добычей, когда опора перестаёт быть твёрдой —
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
