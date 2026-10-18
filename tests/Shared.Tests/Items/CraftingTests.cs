using Shared.Blocks;
using Shared.Items;
using Shared.Voxels;

namespace Shared.Tests.Items;

/// <summary>Обмен предметов в инвентаре (<see cref="Inventory.TryExchange"/>) и правила ремесла (<see cref="Crafting"/>).</summary>
public class CraftingTests
{
    private readonly ItemRegistry _items = new();
    private readonly ItemId _log;
    private readonly ItemId _planks;
    private readonly ItemId _stick;
    private readonly ItemId _table;

    /// <summary>Инструмент: стопка из одной штуки, поэтому каждая занимает свой слот.</summary>
    private readonly ItemId _pickaxe;
    private readonly Inventory _inventory;

    public CraftingTests()
    {
        _log = _items.Register("test:log", 64).Id;
        _planks = _items.Register("test:planks", 64).Id;
        _stick = _items.Register("test:stick", 64).Id;
        _table = _items.Register("test:table", 64).Id;
        _pickaxe = _items.Register("test:pickaxe", 1).Id;
        _items.Freeze();
        _inventory = new Inventory(_items);
    }

    private int Total() => Enumerable.Range(0, Inventory.SlotCount).Sum(slot => _inventory[slot].Count);

    [Fact]
    public void TryExchange_TakesIngredients_GivesResult_AllElseUntouched()
    {
        _inventory.Set(0, new ItemStack(_planks, 10));
        _inventory.Set(12, new ItemStack(_stick, 5));
        _inventory.Set(20, new ItemStack(_log, 7));
        _inventory.TakeChanges();

        Assert.True(_inventory.TryExchange([new ItemStack(_planks, 3), new ItemStack(_stick, 2)], new ItemStack(_pickaxe, 1)));

        Assert.Equal(7, _inventory.Count(_planks));
        Assert.Equal(3, _inventory.Count(_stick));
        Assert.Equal(7, _inventory.Count(_log));
        Assert.Equal(1, _inventory.Count(_pickaxe));
        Assert.Equal(7 + 3 + 7 + 1, Total());
        // Результат — как добыча: в первый пустой слот хотбара.
        Assert.Equal(new ItemStack(_pickaxe, 1), _inventory[1]);
        Assert.Equal([0, 1, 12], _inventory.TakeChanges().Select(change => change.Slot));
    }

    [Fact]
    public void TryExchange_NotEnough_ChangesNothing()
    {
        _inventory.Set(0, new ItemStack(_planks, 10));
        _inventory.Set(5, new ItemStack(_stick, 1));
        _inventory.TakeChanges();

        // Доски есть, а палок не хватает: доски, забранные первыми, должны вернуться.
        Assert.False(_inventory.TryExchange([new ItemStack(_planks, 3), new ItemStack(_stick, 2)], new ItemStack(_pickaxe, 1)));

        Assert.Equal(new ItemStack(_planks, 10), _inventory[0]);
        Assert.Equal(new ItemStack(_stick, 1), _inventory[5]);
        Assert.Empty(_inventory.TakeChanges());
    }

    [Fact]
    public void TryExchange_NoRoomForResult_ChangesNothing()
    {
        for (var slot = 0; slot < Inventory.Size; slot++)
            _inventory.Set(slot, new ItemStack(_pickaxe, 1));
        _inventory.Set(30, new ItemStack(_planks, 10)); // доски тратятся, но слот не освобождается
        _inventory.TakeChanges();

        Assert.False(_inventory.TryExchange([new ItemStack(_planks, 4)], new ItemStack(_table, 1)));

        Assert.Equal(new ItemStack(_planks, 10), _inventory[30]);
        Assert.Empty(_inventory.TakeChanges());
    }

    [Fact]
    public void TryExchange_SpentIngredientFreesSlotForResult()
    {
        for (var slot = 0; slot < Inventory.Size; slot++)
            _inventory.Set(slot, new ItemStack(_pickaxe, 1));
        _inventory.Set(30, new ItemStack(_planks, 4)); // последние доски — их слот и займёт верстак
        _inventory.TakeChanges();

        Assert.True(_inventory.TryExchange([new ItemStack(_planks, 4)], new ItemStack(_table, 1)));

        Assert.Equal(new ItemStack(_table, 1), _inventory[30]);
    }

    [Fact]
    public void TryExchange_TakesFromMainPartBeforeHotbar_IgnoresCursor()
    {
        _inventory.Set(Inventory.CursorSlot, new ItemStack(_planks, 64));
        _inventory.Set(2, new ItemStack(_planks, 5));
        _inventory.Set(20, new ItemStack(_planks, 3));

        Assert.Equal(8, _inventory.Count(_planks)); // курсор не считается
        Assert.True(_inventory.TryExchange([new ItemStack(_planks, 4)], new ItemStack(_stick, 4)));

        Assert.True(_inventory[20].IsEmpty);                      // сначала основная часть
        Assert.Equal(new ItemStack(_planks, 4), _inventory[2]);   // остаток — из хотбара
        Assert.Equal(new ItemStack(_planks, 64), _inventory.Cursor);
        Assert.False(_inventory.TryExchange([new ItemStack(_planks, 5)], new ItemStack(_stick, 4)));
    }

    [Fact]
    public void MaxCrafts_IsLimitedByScarcestIngredient()
    {
        var recipe = new Recipe("test:pickaxe", new ItemStack(_pickaxe, 1), [new ItemStack(_planks, 3), new ItemStack(_stick, 2)], null);
        Assert.Equal(0, Crafting.MaxCrafts(_inventory, recipe));

        _inventory.Set(0, new ItemStack(_planks, 7));
        _inventory.Set(9, new ItemStack(_planks, 2));
        _inventory.Set(1, new ItemStack(_stick, 9));

        // Доски из двух стопок (7 + 2) — дефицитный ингредиент, и он первый: палок хватило бы на 9 / 2 = 4.
        Assert.Equal(3, Crafting.MaxCrafts(_inventory, recipe)); // 9 / 3 = 3
    }

    // Глаз — в центре блока (0, 1, 0), на x = 0,5: до блока (dx, 1, dz) по X — от ближней грани куба.
    [Theory]
    // Досягаемость — 6 (Crafting.StationReach).
    [InlineData(6, 0, true)]    // грань x = 6 — в 5,5
    [InlineData(7, 0, false)]   // 6,5
    [InlineData(6, 1, true)]    // √(5,5² + 0,5²) ≈ 5,52; до центра было бы √(6² + 0,5²) ≈ 6,02
    [InlineData(6, 3, false)]   // по диагонали: √(5,5² + 2,5²) ≈ 6,04
    [InlineData(4, 4, true)]    // √(3,5² + 3,5²) ≈ 4,95
    [InlineData(-6, 0, true)]   // блок −6..−5: грань x = −5 — в 5,5
    [InlineData(-7, 0, false)]  // 6,5
    public void IsStationNear_MeasuresToNearestPointOfBlock(int dx, int dz, bool expected)
    {
        var station = new BlockId(3);
        var other = new BlockId(4);
        var map = new ChunkMap();
        foreach (var x in new[] { -1, 0 })
        foreach (var y in new[] { -1, 0 })
        foreach (var z in new[] { -1, 0 })
            map.Set(new Chunk(new ChunkPos(x, y, z)));
        var eye = new Vector3d(0.5, 1.5, 0.5);
        map.TrySetBlock(new BlockPos(dx, 1, dz), station);
        map.TrySetBlock(new BlockPos(1, 1, 0), other);

        Assert.Equal(expected, Crafting.IsStationNear(map, eye, station));
    }

    [Fact]
    public void IsStationNear_OtherBlockOrUnloadedWorld_IsNotStation()
    {
        var map = new ChunkMap();
        map.Set(new Chunk(new ChunkPos(0, 0, 0)));
        map.TrySetBlock(new BlockPos(1, 1, 1), new BlockId(4));
        var eye = new Vector3d(0.5, 1.5, 0.5);

        Assert.False(Crafting.IsStationNear(map, eye, new BlockId(3)));
        Assert.True(Crafting.IsStationNear(map, eye, new BlockId(4)));
        Assert.False(Crafting.IsStationNear(new ChunkMap(), eye, new BlockId(3)));
    }
}
