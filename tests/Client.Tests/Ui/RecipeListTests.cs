using Client.Ui;
using Engine.Ui;
using Shared.Blocks;
using Shared.Items;
using Silk.NET.Maths;

namespace Client.Tests.Ui;

public class RecipeListTests
{
    private readonly ItemRegistry _items = new();
    private readonly ItemId _log;
    private readonly ItemId _planks;
    private readonly BlockId _table = new(5);
    private readonly BlockId _furnace = new(6);

    public RecipeListTests()
    {
        _log = _items.Register("test:log", 64).Id;
        _planks = _items.Register("test:planks", 64).Id;
        _items.Freeze();
    }

    private Recipe Recipe(string name, ItemId ingredient, BlockId? station = null) =>
        new(name, new ItemStack(_planks, 1), [new ItemStack(ingredient, 1)], station);

    [Fact]
    public void Build_AvailableFirst_RegistryOrderWithinGroups_StationCheckedOncePerKind()
    {
        var recipes = new RecipeRegistry([
            Recipe("test:a_needs_planks", _planks),
            Recipe("test:b_at_furnace", _log, _furnace),
            Recipe("test:c_by_hand", _log),
            Recipe("test:d_at_table", _log, _table),
            Recipe("test:e_at_table_too", _log, _table),
        ]);
        var inventory = new Inventory(_items);
        inventory.Set(3, new ItemStack(_log, 2));
        var checks = new List<BlockId>();

        var entries = RecipeList.Build(recipes, inventory, station =>
        {
            checks.Add(station);
            return station == _table;
        });

        Assert.Equal(["test:c_by_hand", "test:d_at_table", "test:e_at_table_too", "test:a_needs_planks", "test:b_at_furnace"],
            entries.Select(e => e.Recipe.Name));
        Assert.Equal([true, true, true, false, false], entries.Select(e => e.Available));
        Assert.Equal(2, entries[0].Times);
        Assert.False(entries[4].StationNear);
        Assert.Equal(2, entries[4].Times); // брёвна есть, не хватает только печи — подсказка покажет это
        Assert.Equal([_furnace, _table], checks);
    }

    /// <summary>Палитра ванили сейчас — 3 строки (21 предмет); 0 — выживание, палитры нет.</summary>
    [Theory]
    [InlineData(320, 240, 0)]
    [InlineData(320, 240, 3)]
    [InlineData(1280, 720, 0)]
    [InlineData(1280, 720, 3)]
    [InlineData(1920, 1080, 3)]
    [InlineData(1366, 768, 2)]
    public void Panels_FitOnScreen_DoNotOverlap_AreCentered(int width, int height, int paletteRows)
    {
        var screen = new Vector2D<int>(width, height);
        var scale = UiScale.For(width, height);

        var inventory = InventoryScreen.InventoryPanel(screen, scale, paletteRows);
        var recipes = InventoryScreen.RecipePanel(screen, scale, paletteRows);
        var grid = InventoryScreen.GridBounds(screen, scale, paletteRows);

        Assert.True(inventory.X >= 0 && recipes.Right <= width && inventory.Y >= 0 && inventory.Bottom <= height);
        Assert.True(inventory.Right < recipes.X);
        Assert.Equal(inventory.Height, recipes.Height);
        Assert.InRange(width - recipes.Right - inventory.X, -scale, scale);    // по центру с точностью до пикселя интерфейса
        Assert.InRange(height - inventory.Bottom - inventory.Y, -scale, scale);
        // Сетка инвентаря и палитра под ней (заголовок 20, строки по 20) — внутри панели.
        Assert.True(grid.X > inventory.X && grid.Right < inventory.Right && grid.Y > inventory.Y);
        Assert.True(grid.Bottom + (paletteRows > 0 ? (20 + paletteRows * 20) * scale : 0) < inventory.Bottom);

        // Видимые ячейки рецептов — внутри панели, по сетке, без наложений.
        var rows = InventoryScreen.VisibleRecipeRows(screen, scale, paletteRows);
        Assert.True(rows >= 4);
        for (var i = 0; i < rows * InventoryScreen.RecipeColumns; i++)
        {
            var cell = InventoryScreen.RecipeCell(screen, scale, paletteRows, i);
            Assert.True(cell.X > recipes.X && cell.Right < recipes.Right && cell.Y > recipes.Y && cell.Bottom < recipes.Bottom, $"ячейка {i}");
            if (i % InventoryScreen.RecipeColumns > 0)
                Assert.Equal(InventoryScreen.RecipeCell(screen, scale, paletteRows, i - 1).Right, cell.X);
        }
    }

    [Fact]
    public void MaxScroll_CountsWholeRowsBeyondVisible()
    {
        var screen = new Vector2D<int>(1280, 720);
        var scale = UiScale.For(1280, 720);

        Assert.Equal(4, InventoryScreen.VisibleRecipeRows(screen, scale, 0));
        Assert.Equal(0, InventoryScreen.MaxRecipeScroll(16, screen, scale, 0)); // 4 × 4 видно целиком
        Assert.Equal(1, InventoryScreen.MaxRecipeScroll(17, screen, scale, 0));
        Assert.Equal(2, InventoryScreen.MaxRecipeScroll(24, screen, scale, 0));
        Assert.Equal(8, InventoryScreen.VisibleRecipeRows(screen, scale, 3));   // панель выше на палитру
        Assert.Equal(0, InventoryScreen.MaxRecipeScroll(24, screen, scale, 3));
    }
}
