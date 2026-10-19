using Shared.Content;
using System.Numerics;
using Shared.Blocks;
using Shared.Items;
using Shared;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

public sealed class ItemUseTests : IDisposable
{
    private static readonly Vector3 Down = -Vector3.UnitY;

    /// <summary>
    /// Верхний блок столбца (0, 0) и игрок, висящий в полёте ногами на блок выше его верха: блок, поставленный
    /// на поверхность, касается тела, но не пересекает его. Глаз — на <see cref="PlayerPhysics.EyeHeight"/> выше ног.
    /// </summary>
    private readonly BlockPos _surface;
    private readonly BlockPos _aboveSurface;
    private readonly Vector3d _feetAbove;
    private readonly Vector3d _eyeAbove;

    private readonly GameServer _server = TestServers.Create();
    private readonly TestServers.TestClient _client;
    private readonly BlockRegistry _registry;
    private readonly ItemRegistry _items;
    private readonly BaseBlocks _blocks;
    private int PlanksStackSize => _items.Get("base:planks").MaxStackSize;

    /// <summary>Инвентарь игрока на сервере. Тест очищает его и кладёт то, что нужно.</summary>
    private readonly Inventory _inventory;

    /// <summary>Сообщения об изменённых слотах, полученные клиентом в последнем <see cref="Send"/>.</summary>
    private readonly List<InventoryChanged> _inventoryChanges = [];

    public ItemUseTests()
    {
        // Тот же контент, что у сервера: id блоков и предметов совпадают.
        var content = GameContent.Load();
        _registry = content.Blocks;
        _items = content.Items;
        _blocks = BaseBlocks.From(_registry);
        _client = TestServers.JoinAndLoad(_server);
        _surface = TestServers.TopBlock(_server.World, 0, 0);
        _aboveSurface = _surface + new BlockPos(0, 1, 0);
        _feetAbove = new Vector3d(0.5, _surface.Y + 2, 0.5);
        _eyeAbove = EyeOf(_feetAbove);

        // Стартовый набор сервера тестам не нужен: каждый кладёт свои предметы.
        _inventory = _server.PlayerInventory(_client.PlayerId);
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
            _inventory.Set(slot, ItemStack.Empty);
        _inventory.TakeChanges();
    }

    public void Dispose() => _server.Dispose();

    private ItemStack Stack(BlockId block, int count) => new(_items.ForBlock(block), count);

    /// <summary>
    /// Отправляет сообщения от основного клиента, тикает сервер и возвращает полученные изменения блоков. Основное действие
    /// ломает удержанием, поэтому с ним сервер тикает <see cref="TestServers.HoldBreakTicks"/> раз — как если бы ЛКМ держали.
    /// </summary>
    private List<BlockChanged> Send(params ClientMessage[] messages)
    {
        foreach (var message in messages)
            _client.Send(message);
        var holdsPrimary = messages.Any(m => m is UseItem { Action: ItemAction.Primary });

        // Если игрок перешёл в другой чанк, вместе с изменениями могут прийти чанки: они здесь не важны.
        var changes = new List<BlockChanged>();
        _inventoryChanges.Clear();
        for (var tick = 0; tick < (holdsPrimary ? TestServers.HoldBreakTicks : 1); tick++)
        {
            _server.Tick();
            while (_client.TryReceive(out var message))
            {
                if (message is BlockChanged change)
                    changes.Add(change);
                if (message is InventoryChanged inventoryChange)
                    _inventoryChanges.Add(inventoryChange);
            }
        }
        return changes;
    }

    /// <summary>Ставит игрока ногами в <paramref name="feet"/> и отправляет сообщения от его имени.</summary>
    private List<BlockChanged> SendFrom(Vector3d feet, params ClientMessage[] messages)
    {
        TestServers.Teleport(_server, _client, feet);
        return Send(messages);
    }

    private BlockId BlockAt(BlockPos pos) => _server.World.Chunks.GetBlock(pos);

    private static Vector3d EyeOf(Vector3d feet) => feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0);

    private List<BlockChanged> BreakSurface() => SendFrom(_feetAbove, new UseItem(ItemAction.Primary, _eyeAbove, Down));

    private List<BlockChanged> PlaceOnSurface() => SendFrom(_feetAbove, new UseItem(ItemAction.Secondary, _eyeAbove, Down));

    // --- Ломание ---

    [Fact]
    public void Primary_BreaksTargetBlock_AndPutsItsItemIntoInventory()
    {
        var surfaceBlock = BlockAt(_surface);

        var changes = BreakSurface();

        Assert.Equal([new BlockChanged(_surface, BlockId.Air)], changes);
        Assert.True(BlockAt(_surface).IsAir);
        Assert.Equal(Stack(surfaceBlock, 1), _inventory[0]);
        // Клиент узнал о добыче в том же тике.
        Assert.Equal([new InventorySlot(0, Stack(surfaceBlock, 1))], Assert.Single(_inventoryChanges).Slots);
    }

    [Fact]
    public void Primary_AddsToExistingStack_BeforeEmptySlot()
    {
        var surfaceBlock = BlockAt(_surface);
        _inventory.Set(0, Stack(_blocks.Planks, 5));
        _inventory.Set(12, Stack(surfaceBlock, 10));

        BreakSurface();

        Assert.Equal(Stack(surfaceBlock, 11), _inventory[12]);
        Assert.True(_inventory[1].IsEmpty);
    }

    [Fact]
    public void Primary_InventoryFull_BreaksAnyway_DropStaysOnGround()
    {
        var surfaceBlock = BlockAt(_surface);
        Assert.NotEqual(_blocks.Planks, surfaceBlock);
        for (var slot = 0; slot < Inventory.Size; slot++)
            _inventory.Set(slot, Stack(_blocks.Planks, PlanksStackSize));
        _inventory.TakeChanges();

        var changes = BreakSurface();

        Assert.Equal([new BlockChanged(_surface, BlockId.Air)], changes);
        Assert.Empty(_inventoryChanges);
        Assert.Equal(Stack(surfaceBlock, 1), Assert.Single(TestServers.ItemsOnGround(_server)));
    }

    [Fact]
    public void Primary_FullInventory_ButRoomInSameStack_Breaks()
    {
        var surfaceBlock = BlockAt(_surface);
        for (var slot = 0; slot < Inventory.Size; slot++)
            _inventory.Set(slot, Stack(_blocks.Planks, PlanksStackSize));
        _inventory.Set(30, Stack(surfaceBlock, 63));

        Assert.Single(BreakSurface());

        Assert.Equal(Stack(surfaceBlock, 64), _inventory[30]);
    }

    [Fact]
    public void Primary_WithEmptyHand_Breaks()
    {
        Assert.True(_inventory.Selected.IsEmpty);

        Assert.Single(BreakSurface());
    }

    // --- Установка ---

    [Fact]
    public void Secondary_PlacesHeldBlock_AndSpendsOne()
    {
        _inventory.Set(0, Stack(_blocks.Stone, 5));

        var changes = PlaceOnSurface();

        Assert.Equal([new BlockChanged(_aboveSurface, _blocks.Stone)], changes);
        Assert.Equal(Stack(_blocks.Stone, 4), _inventory[0]);
        Assert.Equal([new InventorySlot(0, Stack(_blocks.Stone, 4))], Assert.Single(_inventoryChanges).Slots);
    }

    [Fact]
    public void Secondary_LastItem_FreesSlot_ThenPlacesNothing()
    {
        _inventory.Set(0, Stack(_blocks.Stone, 1));

        Assert.Single(PlaceOnSurface());
        Assert.True(_inventory[0].IsEmpty);

        // Рука пуста: следующий блок не ставится (бесконечных блоков нет).
        Assert.Empty(SendFrom(_feetAbove + new Vector3d(0, 1, 0),
            new UseItem(ItemAction.Secondary, EyeOf(_feetAbove + new Vector3d(0, 1, 0)), Down)));
    }

    // --- Творческий режим ---

    [Fact]
    public void Creative_BreakGivesNothing_PlaceSpendsNothing()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        _inventory.Set(0, Stack(_blocks.Stone, 1));
        _inventory.TakeChanges();

        Assert.Single(PlaceOnSurface());
        Assert.Equal(Stack(_blocks.Stone, 1), _inventory[0]);

        // Ломаем только что поставленный камень: блок ломается, а в инвентаре ничего не прибавилось.
        Assert.Equal([new BlockChanged(_aboveSurface, BlockId.Air)],
            SendFrom(_feetAbove, new UseItem(ItemAction.Primary, _eyeAbove, Down)));
        Assert.Equal(Stack(_blocks.Stone, 1), _inventory[0]);
        Assert.Equal(1, Enumerable.Range(0, Inventory.SlotCount).Count(s => !_inventory[s].IsEmpty));
        Assert.Empty(_inventoryChanges);
    }

    [Fact]
    public void Creative_FullInventory_StillBreaks()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        for (var slot = 0; slot < Inventory.Size; slot++)
            _inventory.Set(slot, Stack(_blocks.Planks, PlanksStackSize));

        Assert.Single(BreakSurface());
    }

    [Fact]
    public void Secondary_WithEmptyHand_PlacesNothing()
    {
        _inventory.Set(1, Stack(_blocks.Stone, 5)); // в инвентаре есть, но не в руке

        Assert.Empty(PlaceOnSurface());
        Assert.True(BlockAt(_aboveSurface).IsAir);
    }

    [Fact]
    public void SelectedSlot_DecidesPlacedBlock()
    {
        _inventory.Set(0, Stack(_blocks.Stone, 5));
        _inventory.Set(8, Stack(_blocks.Planks, 5));

        SendFrom(_feetAbove, new SelectHotbarSlot(8), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Equal(_blocks.Planks, BlockAt(_aboveSurface));
        Assert.Equal(Stack(_blocks.Planks, 4), _inventory[8]);
        Assert.Equal(Stack(_blocks.Stone, 5), _inventory[0]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Inventory.HotbarSize)]
    [InlineData(Inventory.CursorSlot)]
    public void InvalidSlot_IsIgnored(int slot)
    {
        _inventory.Set(0, Stack(_blocks.Stone, 5));

        SendFrom(_feetAbove, new SelectHotbarSlot(slot), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Equal(_blocks.Stone, BlockAt(_aboveSurface));
    }

    [Fact]
    public void CannotPlaceBlockIntoOwnBody_NothingSpent()
    {
        _inventory.Set(0, Stack(_blocks.Stone, 5));
        // Стоит на поверхности: блок над ней — у него в ногах.
        var standing = new Vector3d(0.5, _surface.Y + 1, 0.5);

        var changes = SendFrom(standing, new UseItem(ItemAction.Secondary, EyeOf(standing), Down));

        Assert.Empty(changes);
        Assert.Equal(Stack(_blocks.Stone, 5), _inventory[0]);
    }

    [Fact]
    public void CannotPlaceBlockIntoOtherPlayer()
    {
        _inventory.Set(0, Stack(_blocks.Stone, 5));
        var other = TestServers.JoinAndLoad(_server);
        TestServers.Teleport(_server, other, new Vector3d(0.5, _surface.Y + 1, 0.5));

        Assert.Empty(PlaceOnSurface());
    }

    [Fact]
    public void CanPlaceBlockNextToOtherPlayer()
    {
        _inventory.Set(0, Stack(_blocks.Stone, 5));
        // Второй игрок стоит в соседнем столбце вплотную к месту установки: касание — не пересечение.
        var other = TestServers.JoinAndLoad(_server);
        var otherSurface = TestServers.TopBlock(_server.World, 1, 0);
        TestServers.Teleport(_server, other, new Vector3d(1 + PlayerPhysics.Width / 2, otherSurface.Y + 1, 0.5));

        Assert.Single(PlaceOnSurface());
    }

    // --- Проверки сервера ---

    [Fact]
    public void EyeFarFromPlayer_IsRejected()
    {
        var farAway = _feetAbove + new Vector3(0, 0, 20);
        var before = BlockAt(_surface);

        var changes = SendFrom(farAway, new UseItem(ItemAction.Primary, _eyeAbove, Down));

        Assert.Empty(changes);
        Assert.Equal(before, BlockAt(_surface));
        Assert.True(_inventory[0].IsEmpty);
    }

    [Fact]
    public void NothingInReach_DoesNothing()
    {
        var changes = SendFrom(_feetAbove, new UseItem(ItemAction.Primary, _eyeAbove, Vector3.UnitY));

        Assert.Empty(changes);
    }

    [Fact]
    public void InvalidNumbers_AreIgnored()
    {
        var nan = new Vector3(float.NaN);

        var changes = SendFrom(_feetAbove, new UseItem(ItemAction.Primary, new Vector3d(double.NaN, 0, 0), Down),
            new UseItem(ItemAction.Primary, _eyeAbove, nan),
            new UseItem(ItemAction.Primary, _eyeAbove, Vector3.Zero));

        Assert.Empty(changes);
    }

    [Fact]
    public void BlockChange_IsSentToEveryPlayer()
    {
        var second = TestServers.JoinAndLoad(_server);
        // Второй стоит рядом с добычей: в творческом он её не подбирает, и в его сообщениях — только рассылка мира.
        _server.SetGameMode(second.PlayerId, GameMode.Creative);
        _server.Tick();
        while (second.TryReceive(out _)) { }

        BreakSurface();

        Assert.True(second.TryReceive(out var message));
        Assert.Equal(new BlockChanged(_surface, BlockId.Air), message);
        // Дальше — только свет вокруг ямы и выпавшая добыча: их видят все, у кого есть чанк.
        var rest = new List<ServerMessage>();
        while (second.TryReceive(out var next))
            rest.Add(next);
        Assert.All(rest, m => Assert.True(m is ChunkLightChanged or EntitySpawned or EntitiesMoved or EntityRemoved, m.GetType().Name));
        Assert.Contains(rest, m => m is ChunkLightChanged light && light.Position == _surface.Chunk);
    }

    // --- Клики по инвентарю ---

    [Fact]
    public void InventoryClick_IsAppliedOnServer_AndChangedSlotsSentBack()
    {
        _inventory.Set(3, Stack(_blocks.Stone, 10));
        _inventory.TakeChanges();

        Send(new InventoryClick(3, InventoryClickKind.Secondary), new InventoryClick(20, InventoryClickKind.Primary));

        Assert.Equal(Stack(_blocks.Stone, 5), _inventory[3]);
        Assert.Equal(Stack(_blocks.Stone, 5), _inventory[20]);
        Assert.True(_inventory.Cursor.IsEmpty);
        // Курсор взял половину и положил её в том же тике: он вернулся к прежнему значению, но слот изменения отправлен.
        Assert.Equal(
            [new InventorySlot(3, Stack(_blocks.Stone, 5)), new InventorySlot(20, Stack(_blocks.Stone, 5)), new InventorySlot(Inventory.CursorSlot, ItemStack.Empty)],
            Assert.Single(_inventoryChanges).Slots);
    }

    [Fact]
    public void InvalidInventoryClick_IsIgnored()
    {
        _inventory.Set(0, Stack(_blocks.Stone, 10));
        _inventory.TakeChanges();

        Send(new InventoryClick(Inventory.CursorSlot, InventoryClickKind.Primary), new InventoryClick(-5, InventoryClickKind.Primary),
            new InventoryClick(0, (InventoryClickKind)42));

        Assert.Equal(Stack(_blocks.Stone, 10), _inventory[0]);
        Assert.Empty(_inventoryChanges);
    }
}
