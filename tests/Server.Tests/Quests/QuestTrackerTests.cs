using Api;
using Server.Modding;
using Server.Quests;
using Shared;
using Shared.Blocks;
using Shared.Items;
using Shared.Networking;
using Shared.Protocol;
using Shared.Quests;

namespace Server.Tests.Quests;

/// <summary>Правила счёта заданий — на своём наборе заданий, без мира.</summary>
public class QuestTrackerTests
{
    private readonly ItemRegistry _items = new();
    private readonly ItemId _log;
    private readonly BlockId _stone = new(3);
    private readonly List<QuestCompleted> _completed = [];
    private readonly GameEvents _events = new(Console.WriteLine);
    private readonly ClientSession _session;
    private readonly IConnection<ClientMessage, ServerMessage> _client;

    public QuestTrackerTests()
    {
        _log = _items.Register("test:log", 64).Id;
        _items.Freeze();
        _events.For("test").OnQuestCompleted(_completed.Add);
        _events.Freeze();

        var (client, server) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        _client = client;
        _session = new ClientSession(server);
        _session.Join(1, "Tester", GameMode.Survival, new Inventory(_items), new QuestBook(),
            new PlayerChunkView(1, 1, default));
    }

    private Quest Obtain(string name, int count, params string[] requires) =>
        new(name, name, "", new QuestGoal(QuestGoalKind.Obtain, _log, BlockId.Air, count), [.. requires]);

    private Quest Break(string name, int count, params string[] requires) =>
        new(name, name, "", new QuestGoal(QuestGoalKind.Break, ItemId.Air, _stone, count), [.. requires]);

    private QuestTracker Tracker(params Quest[] quests) => new(new QuestRegistry(quests), _events);

    private void BreakStone(QuestTracker tracker, int amount = 1) =>
        tracker.Count(_session, QuestGoalKind.Break, goal => goal.Block == _stone, amount, world: null!);

    private List<QuestChanged> Messages()
    {
        var messages = new List<QuestChanged>();
        while (_client.TryReceive(out var message))
            messages.Add((QuestChanged)message);
        return messages;
    }

    [Fact]
    public void Count_ProgressesAvailable_CompletesAtGoal_ProgressCapped()
    {
        var tracker = Tracker(Break("test:dig", 3));

        BreakStone(tracker);
        Assert.Equal(1, _session.Quests.Progress["test:dig"]);
        Assert.Equal([new QuestChanged("test:dig", 1, false)], Messages());

        BreakStone(tracker, amount: 10);
        Assert.True(_session.Quests.IsCompleted("test:dig"));
        Assert.Equal([new QuestChanged("test:dig", 3, true)], Messages());

        _events.Dispatch();
        var e = Assert.Single(_completed);
        Assert.Equal("test:dig", e.Quest.Name);
        Assert.Same(_session, e.Player);

        BreakStone(tracker); // выполненное больше не считается
        Assert.Empty(Messages());
    }

    [Fact]
    public void Count_OnlyQuestsAvailableBeforeTheAction()
    {
        // Первое ломание выполняет first и открывает second — но second начинает счёт со следующего.
        var tracker = Tracker(Break("test:first", 1), Break("test:second", 2, "test:first"), Break("test:other", 1, "test:second"));

        BreakStone(tracker);
        Assert.True(_session.Quests.IsCompleted("test:first"));
        Assert.False(_session.Quests.Progress.ContainsKey("test:second"));

        BreakStone(tracker);
        Assert.Equal(1, _session.Quests.Progress["test:second"]);
        Assert.False(_session.Quests.Progress.ContainsKey("test:other")); // недоступно: second ещё не выполнено
    }

    [Fact]
    public void CheckObtain_CompletesChainsAlreadySatisfied_ByInventory()
    {
        var tracker = Tracker(Obtain("test:a", 2), Obtain("test:b", 5, "test:a"), Obtain("test:c", 6, "test:b"));
        _session.Inventory.Set(Inventory.CursorSlot, new ItemStack(_log, 64)); // курсор не считается
        tracker.CheckObtain(_session, world: null!);
        Assert.Empty(_session.Quests.Completed);

        _session.Inventory.Set(0, new ItemStack(_log, 5));
        tracker.CheckObtain(_session, world: null!);

        Assert.Equal(["test:a", "test:b"], _session.Quests.Completed.Order());
        Assert.Equal(["test:a", "test:b"], Messages().Select(m => m.Quest));
    }

    [Fact]
    public void Breaking_CompletingObtain_ThenObtainUnlocked_ByCount()
    {
        // Выполненное по счёту задание открывает «иметь предмет», который уже в инвентаре: он выполняется тем же действием.
        var tracker = Tracker(Break("test:dig", 1), Obtain("test:have", 1, "test:dig"));
        _session.Inventory.Set(0, new ItemStack(_log, 1));

        BreakStone(tracker);

        Assert.True(_session.Quests.IsCompleted("test:have"));
    }

    [Fact]
    public void Creative_DoesNotProgress()
    {
        var tracker = Tracker(Break("test:dig", 1), Obtain("test:have", 1));
        _session.SetMode(GameMode.Creative);
        _session.Inventory.Set(0, new ItemStack(_log, 1));

        BreakStone(tracker);
        tracker.CheckObtain(_session, world: null!);

        Assert.Empty(_session.Quests.Completed);
        Assert.Empty(_session.Quests.Progress);
        Assert.Empty(Messages());
    }
}
