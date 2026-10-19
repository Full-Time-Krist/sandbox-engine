using System.Numerics;
using Api;
using Server.Modding;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests.Modding;

/// <summary>События мира на настоящем сервере: тестовые моды подписываются так же, как будут сторонние.</summary>
public sealed class ModEventsTests : IDisposable
{
    private static readonly Vector3 Down = new(0, -1, 0);

    private readonly ContentRegistries _content = GameContent.Load();
    private readonly List<string> _log = [];
    private readonly List<BlockBroken> _broken = [];
    private readonly List<BlockPlaced> _placed = [];
    private readonly List<PlayerJoined> _joined = [];

    private readonly List<ItemPickedUp> _pickedUp = [];

    /// <summary>Пусто ли на месте блока, когда пришло событие ломания: обработчик должен видеть действие законченным.</summary>
    private readonly List<bool> _airSeenByHandler = [];

    /// <summary>Сколько штук подобранного предмета было в инвентаре, когда пришло событие подбора.</summary>
    private readonly List<int> _countSeenOnPickup = [];

    private GameServer _server = null!;
    private TestServers.TestClient _client = null!;
    private Inventory _inventory = null!;
    private BlockPos _surface;

    public void Dispose() => _server.Dispose();

    private sealed class TestMod(Action<IModContext> load) : IMod
    {
        public void Load(IModContext context) => load(context);
    }

    /// <summary>Мод, записывающий все события.</summary>
    private TestMod Recorder() => new(context =>
    {
        context.Events.OnBlockBroken(e =>
        {
            _broken.Add(e);
            _airSeenByHandler.Add(e.World.GetBlock(e.Position).IsAir);
        });
        context.Events.OnBlockPlaced(_placed.Add);
        context.Events.OnPlayerJoined(_joined.Add);
        context.Events.OnItemPickedUp(e =>
        {
            _pickedUp.Add(e);
            _countSeenOnPickup.Add(e.Player.Inventory.Count(e.Stack.Item));
        });
    });

    private void Start(params (string Id, IMod Mod)[] mods)
    {
        _server = TestServers.Create(mods: mods, modLog: _log.Add);
        _client = TestServers.JoinAndLoad(_server);
        _surface = TestServers.TopBlock(_server.World, 0, 0);
        _inventory = _server.PlayerInventory(_client.PlayerId);
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
            _inventory.Set(slot, ItemStack.Empty);
        _inventory.TakeChanges();
    }

    private void Use(ItemAction action)
    {
        var feet = new Vector3d(0.5, _surface.Y + 2, 0.5);
        TestServers.Teleport(_server, _client, feet);
        _client.Send(new UseItem(action, feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), Down));
        // ЛКМ ломает удержанием — держим, пока блок у поверхности наверняка сломается.
        TestServers.Tick(_server, action == ItemAction.Primary ? TestServers.HoldBreakTicks : 1);
        while (_client.TryReceive(out _)) { }
    }

    private BlockId BlockAt(BlockPos pos) => _server.World.Chunks.GetBlock(pos);

    [Fact]
    public void Break_RaisesBlockBrokenOnce_AfterBlockIsGone()
    {
        Start(("test_mod", Recorder()));
        var surfaceBlock = BlockAt(_surface);

        Use(ItemAction.Primary);

        var e = Assert.Single(_broken);
        Assert.Equal(_surface, e.Position);
        Assert.Equal(surfaceBlock, e.Block);
        Assert.Equal("Tester", e.Player.Name);
        Assert.Equal([true], _airSeenByHandler);
        Assert.Empty(_placed);
    }

    /// <summary>Подбор добычи — событие с подобранной стопкой; обработчик видит её уже в инвентаре.</summary>
    [Fact]
    public void PickUp_RaisesItemPickedUp_AfterItemIsInInventory()
    {
        Start(("test_mod", Recorder()));
        var drop = _content.Items.ForBlock(BlockAt(_surface));

        Use(ItemAction.Primary);
        TestServers.TickUntilPickedUp(_server);

        var e = Assert.Single(_pickedUp);
        Assert.Equal(new ItemStack(drop, 1), e.Stack);
        Assert.Equal("Tester", e.Player.Name);
        Assert.Equal([1], _countSeenOnPickup);
    }

    [Fact]
    public void Break_InventoryFull_StillBreaks_WithEvent()
    {
        Start(("test_mod", Recorder()));
        var planks = _content.Items.Get("base:planks");
        for (var slot = 0; slot < Inventory.Size; slot++)
            _inventory.Set(slot, new ItemStack(planks.Id, planks.MaxStackSize));

        Use(ItemAction.Primary);

        // С шага 7.3 добыча падает на землю, поэтому полный инвентарь ломанию не мешает.
        Assert.True(BlockAt(_surface).IsAir);
        Assert.Single(_broken);
    }

    [Fact]
    public void Place_RaisesBlockPlaced_WithPlacedBlock_NothingWithEmptyHand()
    {
        Start(("test_mod", Recorder()));
        var stone = _content.Items.Get("base:stone");

        Use(ItemAction.Secondary); // пустая рука ничего не ставит
        Assert.Empty(_placed);

        _inventory.Set(0, new ItemStack(stone.Id, 1));
        Use(ItemAction.Secondary);

        var e = Assert.Single(_placed);
        Assert.Equal(_surface + new BlockPos(0, 1, 0), e.Position);
        Assert.Equal(stone.Block, e.Block);
        Assert.Empty(_broken);
    }

    [Fact]
    public void PlayerJoined_RaisedOnce_AndHandlerInventoryChangeReachesClient()
    {
        var stone = _content.Items.Get("base:stone");
        var starter = new TestMod(context => context.Events.OnPlayerJoined(e => e.Player.Inventory.Add(new ItemStack(stone.Id, 3))));
        _server = TestServers.Create(mods: [("test_mod", Recorder()), ("starter_mod", starter)], modLog: _log.Add);

        var received = new List<ServerMessage>();
        _client = TestServers.JoinAndLoad(_server, received);

        Assert.Equal("Tester", Assert.Single(_joined).Player.Name);
        var change = Assert.Single(received.OfType<InventoryChanged>());
        Assert.Equal([new InventorySlot(0, new ItemStack(stone.Id, 3))], change.Slots);
    }

    [Fact]
    public void HandlerException_IsLoggedWithModId_OtherModsAndGameContinue()
    {
        var bad = new TestMod(context => context.Events.OnBlockBroken(_ => throw new InvalidOperationException("сломался")));
        Start(("bad_mod", bad), ("test_mod", Recorder()));

        Use(ItemAction.Primary);

        Assert.True(BlockAt(_surface).IsAir);
        Assert.Single(_broken);
        var line = Assert.Single(_log);
        Assert.Contains("[Mod bad_mod]", line);
        Assert.Contains(nameof(BlockBroken), line);
        Assert.Contains("сломался", line);
    }

    [Fact]
    public void EndlessChainOfEvents_IsCutAtLimit_ServerKeepsRunning()
    {
        // Ломание ставит блок обратно, установка снова ломает: без лимита сервер повис бы в этом тике.
        var pingPong = new TestMod(context =>
        {
            context.Events.OnBlockBroken(e => e.World.TryPlaceBlock(e.Position, e.Block, e.Player));
            context.Events.OnBlockPlaced(e => e.World.TryBreakBlock(e.Position, e.Player));
        });
        Start(("ping_pong", pingPong));

        Use(ItemAction.Primary);
        _server.Tick();

        Assert.Contains(_log, line => line.Contains($"больше {GameEvents.MaxEventsPerDispatch} событий"));
    }

    [Fact]
    public void Subscribing_AfterLoad_Throws()
    {
        IModContext? saved = null;
        Start(("late_mod", new TestMod(context => saved = context)));

        var exception = Assert.Throws<InvalidOperationException>(() => saved!.Events.OnBlockBroken(_ => { }));
        Assert.Contains("'late_mod'", exception.Message);
    }
}
