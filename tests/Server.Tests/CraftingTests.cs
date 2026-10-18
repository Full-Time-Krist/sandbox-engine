using Api;
using Shared;
using Shared.Content;
using Shared.Items;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

/// <summary>Ремесло на настоящем сервере: клиент шлёт только имя рецепта, остальное сервер проверяет сам.</summary>
public sealed class CraftingTests : IDisposable
{
    private readonly ContentRegistries _content = GameContent.Load();
    private readonly List<string> _log = [];
    private readonly List<(ItemCrafted Event, int ResultInInventory)> _crafted = [];
    private readonly GameServer _server;
    private readonly TestServers.TestClient _client;
    private readonly Inventory _inventory;
    private readonly BlockPos _surface;

    public CraftingTests()
    {
        _server = TestServers.Create(mods: [("test_mod", new Recorder(this))], modLog: _log.Add);
        _client = TestServers.JoinAndLoad(_server);
        _surface = TestServers.TopBlock(_server.World, 0, 0);
        // Ноги на поверхности над (0, 0): глаз в (0,5; поверхность + 2,62; 0,5).
        TestServers.Teleport(_server, _client, new Vector3d(0.5, _surface.Y + 1, 0.5));
        _inventory = _server.PlayerInventory(_client.PlayerId);
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
            _inventory.Set(slot, ItemStack.Empty);
        _inventory.TakeChanges();
        Drain();
    }

    public void Dispose() => _server.Dispose();

    /// <summary>Мод, записывающий события ремесла и сколько результата уже было в инвентаре, когда событие пришло.</summary>
    private sealed class Recorder(CraftingTests test) : IMod
    {
        public void Load(IModContext context) => context.Events.OnItemCrafted(e =>
            test._crafted.Add((e, e.Player.Inventory.Count(e.Recipe.Result.Item))));
    }

    private ItemId Item(string name) => _content.Items.Get(name).Id;
    private ItemStack Stack(string name, int count) => new(Item(name), count);

    private List<ServerMessage> Drain()
    {
        var messages = new List<ServerMessage>();
        while (_client.TryReceive(out var message))
            messages.Add(message);
        return messages;
    }

    private List<ServerMessage> Craft(string recipe, bool all = false)
    {
        _client.Send(new CraftRecipe(recipe, all));
        _server.Tick();
        return Drain();
    }

    /// <summary>
    /// Ставит станок на уровне ног игрока со смещением от его столбца. Высота — от игрока, а не от рельефа: рядом может
    /// стоять дерево, и станок на его кроне оказался бы далеко по вертикали.
    /// </summary>
    private void PlaceStation(string block, int dx, int dz) =>
        Assert.True(_server.World.TrySetBlock(new BlockPos(dx, _surface.Y + 1, dz), _content.Blocks.Get(block).Id));

    [Fact]
    public void HandRecipe_SpendsIngredients_GivesResult_ClientGetsChangedSlots()
    {
        _inventory.Set(0, Stack("base:log", 3));
        _inventory.TakeChanges();

        var messages = Craft("base:planks");

        Assert.Equal(2, _inventory.Count(Item("base:log")));
        Assert.Equal(4, _inventory.Count(Item("base:planks")));
        var changed = Assert.Single(messages.OfType<InventoryChanged>());
        Assert.Equal([new InventorySlot(0, Stack("base:log", 2)), new InventorySlot(1, Stack("base:planks", 4))], changed.Slots);
    }

    [Fact]
    public void NotEnoughIngredients_ChangesNothing()
    {
        _inventory.Set(0, Stack("base:planks", 1));
        _inventory.TakeChanges();

        var messages = Craft("base:stick"); // нужно 2 доски

        Assert.Equal(Stack("base:planks", 1), _inventory[0]);
        Assert.Empty(messages.OfType<InventoryChanged>());
        Assert.Empty(_crafted);
    }

    [Fact]
    public void StationRecipe_NeedsStationWithinReach()
    {
        _inventory.Set(0, Stack("base:planks", 9));
        _inventory.Set(1, Stack("base:stick", 6));
        var pickaxe = Item("base:wooden_pickaxe");

        Craft("base:wooden_pickaxe");
        Assert.Equal(0, _inventory.Count(pickaxe)); // верстака нет

        PlaceStation("base:crafting_table", 8, 0);  // в 7,5 блока по горизонтали — дальше досягаемости (6)
        Craft("base:wooden_pickaxe");
        Assert.Equal(0, _inventory.Count(pickaxe));

        PlaceStation("base:furnace", 2, 0);         // рядом, но не тот станок
        Craft("base:wooden_pickaxe");
        Assert.Equal(0, _inventory.Count(pickaxe));

        PlaceStation("base:crafting_table", 0, 2);
        Craft("base:wooden_pickaxe");
        Assert.Equal(1, _inventory.Count(pickaxe));
        Assert.Equal(6, _inventory.Count(Item("base:planks")));
        Assert.Equal(4, _inventory.Count(Item("base:stick")));
    }

    [Fact]
    public void All_CraftsWhileIngredientsLast_OneEventWithTimes_AfterResultIsInInventory()
    {
        _inventory.Set(0, Stack("base:log", 5));

        Craft("base:planks", all: true);

        Assert.Equal(0, _inventory.Count(Item("base:log")));
        Assert.Equal(20, _inventory.Count(Item("base:planks")));
        var (e, resultInInventory) = Assert.Single(_crafted);
        Assert.Equal("base:planks", e.Recipe.Name);
        Assert.Equal(5, e.Times);
        Assert.Equal(20, resultInInventory);
        Assert.Equal("Tester", e.Player.Name);
    }

    [Fact]
    public void All_StopsWhenResultNoLongerFits_ItemsAreConserved()
    {
        // 35 слотов заняты кирками, в последнем — 3 бревна: доски помещаются только в слот, освобождённый брёвнами.
        for (var slot = 0; slot < Inventory.Size - 1; slot++)
            _inventory.Set(slot, Stack("base:wooden_pickaxe", 1));
        _inventory.Set(Inventory.Size - 1, Stack("base:log", 3));

        Craft("base:planks", all: true);
        Assert.Equal(3, _inventory.Count(Item("base:log")));  // 4 доски не влезают, пока бревно не освобождает слот
        Assert.Empty(_crafted);

        _inventory.Set(Inventory.Size - 1, Stack("base:log", 1));
        Craft("base:planks", all: true);
        Assert.Equal(Stack("base:planks", 4), _inventory[Inventory.Size - 1]);
        Assert.Equal(1, Assert.Single(_crafted).Event.Times);
    }

    [Fact]
    public void UnknownOrMissingRecipeName_IsIgnored_ServerKeepsWorking()
    {
        _inventory.Set(0, Stack("base:log", 1));

        Craft("base:diamond_pickaxe");
        Craft(null!); // имя не пришло вовсе — сообщение от чужого клиента
        Assert.Equal(Stack("base:log", 1), _inventory[0]);

        Craft("base:planks");
        Assert.Equal(4, _inventory.Count(Item("base:planks")));
    }
}
