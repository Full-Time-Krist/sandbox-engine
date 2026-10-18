using System.Numerics;
using Server.Tests.Storage;
using Shared;
using Shared.Content;
using Shared.Items;
using Shared.Physics;
using Shared.Protocol;
using Shared.Quests;

namespace Server.Tests.Quests;

/// <summary>Ванильные задания на настоящем сервере: действия игрока продвигают их, они переживают перезапуск.</summary>
public sealed class QuestServerTests : IDisposable
{
    private readonly ContentRegistries _content = GameContent.Load();
    private readonly TempDirectory _directory = new();
    private GameServer _server = null!;
    private TestServers.TestClient _client = null!;
    private readonly List<ServerMessage> _joinMessages = [];

    public void Dispose()
    {
        _server?.Dispose();
        _directory.Dispose();
    }

    private void Start(GameMode mode = GameMode.Survival)
    {
        _server = TestServers.Create(savePath: _directory.SaveFile, defaultMode: mode);
        _joinMessages.Clear();
        _client = TestServers.JoinAndLoad(_server, _joinMessages);
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
            Inventory.Set(slot, ItemStack.Empty);
        Drain();
    }

    private Inventory Inventory => _server.PlayerInventory(_client.PlayerId);
    private QuestBook Quests => _server.PlayerQuests(_client.PlayerId);
    private ItemStack Stack(string name, int count) => new(_content.Items.Get(name).Id, count);

    private List<QuestChanged> Drain()
    {
        var changes = new List<QuestChanged>();
        while (_client.TryReceive(out var message))
        {
            if (message is QuestChanged change)
                changes.Add(change);
        }
        return changes;
    }

    private List<QuestChanged> Tick(ClientMessage? message = null)
    {
        if (message != null)
            _client.Send(message);
        _server.Tick();
        return Drain();
    }

    [Fact]
    public void NewPlayer_GetsEmptyBook_ObtainingLogsCompletesFirstQuest()
    {
        Start();
        var book = Assert.Single(_joinMessages.OfType<QuestBookContents>());
        Assert.Empty(book.Completed);
        Assert.Empty(book.Progress);

        Inventory.Set(0, Stack("base:log", 2));
        Assert.Empty(Tick());
        Inventory.Set(0, Stack("base:log", 3));

        Assert.Equal([new QuestChanged("base:get_wood", 3, Completed: true)], Tick());
        Assert.True(Quests.IsCompleted("base:get_wood"));
    }

    [Fact]
    public void Crafting_CountsResultItems_OnlyOnceQuestIsAvailable()
    {
        Start();
        Inventory.Set(0, Stack("base:log", 2)); // меньше трёх: «Лесоруб» не выполнен, «Доски» недоступны
        Tick(new CraftRecipe("base:planks", All: false));
        Assert.False(Quests.Progress.ContainsKey("base:planks"));

        Inventory.Set(1, Stack("base:log", 3));
        Tick();
        Assert.True(Quests.IsCompleted("base:get_wood"));

        // Одно бревно — 4 доски: этого хватает на всё задание.
        var changes = Tick(new CraftRecipe("base:planks", All: false));
        Assert.Contains(new QuestChanged("base:planks", 4, Completed: true), changes);
    }

    [Fact]
    public void PlacingCraftingTable_CompletesPlaceQuest()
    {
        Start();
        Quests.Complete("base:get_wood");
        Quests.Complete("base:planks");
        Inventory.Set(0, Stack("base:crafting_table", 1));
        var surface = TestServers.TopBlock(_server.World, 0, 0);
        var feet = new Vector3d(0.5, surface.Y + 2, 0.5);
        TestServers.Teleport(_server, _client, feet);

        var changes = Tick(new UseItem(ItemAction.Secondary, feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), new Vector3(0, -1, 0)));

        Assert.Equal(_content.Blocks.Get("base:crafting_table").Id, _server.World.Chunks.GetBlock(surface with { Y = surface.Y + 1 }));
        Assert.Contains(new QuestChanged("base:crafting_table", 1, Completed: true), changes);
    }

    [Fact]
    public void Quests_SurviveRestart()
    {
        Start();
        Quests.Complete("base:get_wood");
        Quests.SetProgress("base:planks", 2);
        _server.Dispose();

        Start();

        var book = Assert.Single(_joinMessages.OfType<QuestBookContents>());
        Assert.Equal(["base:get_wood"], book.Completed);
        Assert.Equal([new QuestCount("base:planks", 2)], book.Progress);
        Assert.True(Quests.IsCompleted("base:get_wood"));
    }

    [Fact]
    public void Creative_QuestsDoNotProgress()
    {
        Start(GameMode.Creative);
        Inventory.Set(0, Stack("base:log", 64));

        Assert.Empty(Tick());
        Assert.Empty(Quests.Completed);
    }
}
