using Shared.Blocks;
using Shared.Items;
using Shared.Quests;

namespace Shared.Tests.Quests;

public class QuestBookTests
{
    private readonly ItemRegistry _items = new();
    private readonly ItemId _log;

    public QuestBookTests()
    {
        _log = _items.Register("test:log", 64).Id;
        _items.Freeze();
    }

    private Quest Quest(string name, QuestGoalKind kind = QuestGoalKind.Craft, int count = 5, params string[] requires) =>
        new(name, name, "", new QuestGoal(kind, _log, BlockId.Air, count), [.. requires]);

    [Fact]
    public void Available_WhenNotDone_AndAllRequiredDone()
    {
        var book = new QuestBook();
        var first = Quest("test:a");
        var second = Quest("test:c", requires: ["test:a", "test:b"]);

        Assert.True(book.IsAvailable(first));
        Assert.False(book.IsAvailable(second));
        Assert.True(book.Complete("test:a"));
        Assert.False(book.Complete("test:a")); // повторно — уже выполнено
        Assert.False(book.IsAvailable(first));
        Assert.False(book.IsAvailable(second)); // test:b ещё нет
        book.Complete("test:b");
        Assert.True(book.IsAvailable(second));
    }

    [Fact]
    public void Progress_IsCountedOrFromInventory_CompletionDropsIt()
    {
        var book = new QuestBook();
        var inventory = new Inventory(_items);
        inventory.Set(3, new ItemStack(_log, 4));
        inventory.Set(Inventory.CursorSlot, new ItemStack(_log, 10));
        var counted = Quest("test:craft");
        var obtain = Quest("test:obtain", QuestGoalKind.Obtain);

        book.SetProgress("test:craft", 2);
        Assert.Equal(2, book.ProgressOf(counted, inventory));
        Assert.Equal(4, book.ProgressOf(obtain, inventory)); // курсор не считается

        book.Complete("test:craft");
        Assert.Empty(book.Progress);
        book.SetProgress("test:other", 3);
        book.SetProgress("test:other", 0);
        Assert.Empty(book.Progress);
        Assert.Throws<ArgumentOutOfRangeException>(() => book.SetProgress("test:other", -1));
    }
}
