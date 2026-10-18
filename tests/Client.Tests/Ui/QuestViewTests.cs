using Client.Ui;
using Engine.Ui;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Quests;
using Silk.NET.Maths;

namespace Client.Tests.Ui;

public class QuestViewTests
{
    private readonly ContentRegistries _content = GameContent.Load();

    private Quest Quest(string name) => _content.Quests.Get(name);

    [Fact]
    public void Icon_IsGoalItem_OrItemOfGoalBlock()
    {
        Assert.Equal(_content.Items.Get("base:log").Id, QuestView.Icon(Quest("base:get_wood"), _content.Items));
        Assert.Equal(_content.Items.Get("base:crafting_table").Id, QuestView.Icon(Quest("base:crafting_table"), _content.Items)); // place
        var noItem = new Quest("test:q", "", "", new QuestGoal(QuestGoalKind.Break, ItemId.Air, new BlockId(999), 1), []);
        Assert.True(QuestView.Icon(noItem, _content.Items).IsAir);
    }

    [Fact]
    public void ProgressText_CappedAtGoal_EmptyForSingleItemGoal()
    {
        var book = new QuestBook();
        var inventory = new Inventory(_content.Items);
        inventory.Set(0, new ItemStack(_content.Items.Get("base:log").Id, 64));

        Assert.Equal("3/3", QuestView.ProgressText(Quest("base:get_wood"), book, inventory)); // «иметь» — из инвентаря, не больше цели
        book.SetProgress("base:planks", 2);
        Assert.Equal("2/4", QuestView.ProgressText(Quest("base:planks"), book, inventory));
        book.Complete("base:planks");
        Assert.Equal("4/4", QuestView.ProgressText(Quest("base:planks"), book, inventory));
        Assert.Equal("", QuestView.ProgressText(Quest("base:crafting_table"), book, inventory));
    }

    [Fact]
    public void Journal_AvailableThenCompleted_RestHidden()
    {
        var book = new QuestBook();
        book.Complete("base:get_wood");
        book.Complete("base:planks");

        var (entries, hidden) = QuestView.Journal(_content.Quests, book);

        Assert.Equal(["base:crafting_table", "base:get_wood", "base:planks"], entries.Select(q => q.Name));
        Assert.Equal(_content.Quests.All.Length - 3, hidden);
    }

    /// <summary>Ширина — число букв: так видно, где режется строка.</summary>
    [Theory]
    [InlineData("один два три", 8, 2, new[] { "один два", "три" })]
    [InlineData("один два три четыре", 8, 2, new[] { "один два", "три…" })]
    [InlineData("один два три четыре", 9, 2, new[] { "один два", "три…" })]
    [InlineData("сверхдлинноеслово", 6, 3, new[] { "сверхд", "линное", "слово" })]
    [InlineData("", 10, 2, new string[0])]
    public void Wrap_ByWords_EllipsisWhenTooManyLines(string text, int width, int maxLines, string[] expected)
    {
        var lines = QuestView.Wrap(text, width, maxLines, s => s.Length);

        Assert.Equal(expected, lines);
        Assert.All(lines, line => Assert.True(line.Length <= width, line));
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(640, 480)]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    public void JournalPanel_FitsAndRowsAreInside(int width, int height)
    {
        var screen = new Vector2D<int>(width, height);
        var scale = UiScale.For(width, height);
        var panel = JournalScreen.PanelBounds(screen, scale);

        Assert.True(panel.X >= 0 && panel.Y >= 0 && panel.Right <= width && panel.Bottom <= height);
        Assert.True(JournalScreen.VisibleRows >= 5);
        for (var i = 0; i < JournalScreen.VisibleRows; i++)
        {
            var row = JournalScreen.RowBounds(screen, scale, i);
            Assert.True(row.X > panel.X && row.Right < panel.Right && row.Y > panel.Y && row.Bottom < panel.Bottom, $"строка {i}");
        }
        Assert.Equal(_content.Quests.All.Length - JournalScreen.VisibleRows, JournalScreen.MaxScroll(_content.Quests.All.Length));
    }

    [Theory]
    [InlineData(320, 1, 172)]
    [InlineData(1280, 3, 516)]
    [InlineData(320, 1, 400)] // шире экрана — урезается
    public void Toast_InTopRightCorner_InsideScreen(int screenWidth, int scale, int width)
    {
        var toast = HudLayout.Toast(screenWidth, width, scale);

        Assert.Equal(screenWidth - HudLayout.HotbarMargin * scale, toast.Right);
        Assert.Equal(HudLayout.HotbarMargin * scale, toast.Y);
        Assert.True(toast.X >= HudLayout.HotbarMargin * scale);
    }
}
