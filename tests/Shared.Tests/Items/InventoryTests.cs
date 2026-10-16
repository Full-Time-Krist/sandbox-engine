using Shared.Blocks;
using Shared.Items;

namespace Shared.Tests.Items;

public class InventoryTests
{
    private const int Cursor = Inventory.CursorSlot;

    private readonly ItemRegistry _items = new();
    private readonly ItemId _stone;
    private readonly ItemId _dirt;

    /// <summary>Предмет со своим пределом стопки (16), чтобы предел брался из предмета, а не был зашит как 64.</summary>
    private readonly ItemId _pearl;

    private readonly Inventory _inventory;

    public InventoryTests()
    {
        var blocks = new BlockRegistry();
        BaseBlocks.Register(blocks);
        blocks.Freeze();
        _items.RegisterBlockItems(blocks);
        _pearl = _items.Register("test:pearl", 16).Id;
        _items.Freeze();
        _stone = _items.Get("base:stone").Id;
        _dirt = _items.Get("base:dirt").Id;
        _inventory = new Inventory(_items);
    }

    private ItemStack Stone(int count) => new(_stone, count);
    private ItemStack Dirt(int count) => new(_dirt, count);

    /// <summary>Заполняет слоты [from, to) стопкой и забывает изменения.</summary>
    private void Fill(int from, int to, ItemStack stack)
    {
        for (var slot = from; slot < to; slot++)
            _inventory.Set(slot, stack);
        _inventory.TakeChanges();
    }

    /// <summary>Непустые слоты (и курсор) с содержимым: проверяем состав целиком, а не только один слот.</summary>
    private Dictionary<int, ItemStack> Contents()
    {
        var contents = new Dictionary<int, ItemStack>();
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
        {
            if (!_inventory[slot].IsEmpty)
                contents[slot] = _inventory[slot];
        }
        return contents;
    }

    private void Click(int slot, InventoryClickKind kind) => Assert.True(_inventory.Click(slot, kind));

    // --- Добыча: Add и CanAdd ---

    [Fact]
    public void Add_ToEmptyInventory_GoesToFirstHotbarSlot()
    {
        Assert.Equal(0, _inventory.Add(Stone(5)));

        Assert.Equal(new() { [0] = Stone(5) }, Contents());
    }

    [Fact]
    public void Add_MergesIntoExistingStack_EvenInMainPart_BeforeEmptyHotbarSlot()
    {
        _inventory.Set(20, Stone(10));

        _inventory.Add(Stone(5));

        Assert.Equal(new() { [20] = Stone(15) }, Contents());
    }

    [Fact]
    public void Add_FillsStackUpToLimit_RestGoesToFirstEmptySlot()
    {
        _inventory.Set(0, Stone(60));
        _inventory.Set(1, Dirt(3));

        Assert.Equal(0, _inventory.Add(Stone(10)));

        Assert.Equal(new() { [0] = Stone(64), [1] = Dirt(3), [2] = Stone(6) }, Contents());
    }

    [Fact]
    public void Add_UsesItemsOwnStackLimit()
    {
        _inventory.Add(new ItemStack(_pearl, 20));

        Assert.Equal(new() { [0] = new ItemStack(_pearl, 16), [1] = new ItemStack(_pearl, 4) }, Contents());
    }

    [Fact]
    public void Add_HotbarFull_GoesToFirstMainSlot()
    {
        Fill(0, Inventory.HotbarSize, Dirt(64));

        _inventory.Add(Stone(1));

        Assert.Equal(Stone(1), _inventory[Inventory.HotbarSize]);
    }

    [Fact]
    public void Add_InventoryFull_ReturnsRemainder()
    {
        Fill(0, Inventory.Size, Dirt(64));
        _inventory.Set(5, Stone(63));

        Assert.True(_inventory.CanAdd(Stone(1)));
        Assert.False(_inventory.CanAdd(Stone(2)));
        Assert.False(_inventory.CanAdd(Dirt(1)));
        Assert.True(_inventory.CanAdd(ItemStack.Empty));

        Assert.Equal(1, _inventory.Add(Stone(2)));
        Assert.Equal(Stone(64), _inventory[5]);
        Assert.True(_inventory.Cursor.IsEmpty);
    }

    [Fact]
    public void CanAdd_ChangesNothing()
    {
        _inventory.Set(0, Stone(10));
        _inventory.TakeChanges();

        Assert.True(_inventory.CanAdd(Stone(100)));

        Assert.Empty(_inventory.TakeChanges());
        Assert.Equal(new() { [0] = Stone(10) }, Contents());
    }

    // --- Трата, выбор слота, прямая запись ---

    [Fact]
    public void Remove_LastItem_FreesSlot()
    {
        _inventory.Set(0, Stone(2));

        _inventory.Remove(0, 1);
        Assert.Equal(Stone(1), _inventory[0]);

        _inventory.Remove(0, 1);
        Assert.Equal(ItemStack.Empty, _inventory[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _inventory.Remove(0, 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Inventory.HotbarSize)]
    [InlineData(Inventory.CursorSlot)]
    [InlineData(int.MaxValue)]
    public void TrySelect_OutsideHotbar_KeepsSelection(int slot)
    {
        Assert.True(_inventory.TrySelect(8));

        Assert.False(_inventory.TrySelect(slot));
        Assert.Equal(8, _inventory.SelectedSlot);
    }

    [Fact]
    public void Selected_IsStackInSelectedSlot()
    {
        _inventory.Set(4, Dirt(7));

        _inventory.TrySelect(4);

        Assert.Equal(Dirt(7), _inventory.Selected);
    }

    [Fact]
    public void Set_RejectsWrongStacksAndSlots()
    {
        Assert.Throws<ArgumentException>(() => _inventory.Set(0, Stone(65)));
        Assert.Throws<ArgumentException>(() => _inventory.Set(0, new ItemStack(_pearl, 17)));
        Assert.Throws<ArgumentException>(() => _inventory.Set(0, new ItemStack(new ItemId(999), 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _inventory.Set(-1, Stone(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _inventory.Set(Inventory.SlotCount, Stone(1)));
        Assert.Empty(Contents());
    }

    // --- Клики ЛКМ ---

    [Fact]
    public void Primary_EmptyCursor_TakesWholeStack()
    {
        _inventory.Set(3, Stone(10));

        Click(3, InventoryClickKind.Primary);

        Assert.Equal(new() { [Cursor] = Stone(10) }, Contents());
    }

    [Fact]
    public void Primary_OnEmptySlot_PutsWholeStack()
    {
        _inventory.Set(Cursor, Stone(10));

        Click(7, InventoryClickKind.Primary);

        Assert.Equal(new() { [7] = Stone(10) }, Contents());
    }

    [Fact]
    public void Primary_DifferentItem_Swaps()
    {
        _inventory.Set(1, Dirt(5));
        _inventory.Set(Cursor, Stone(10));

        Click(1, InventoryClickKind.Primary);

        Assert.Equal(new() { [1] = Stone(10), [Cursor] = Dirt(5) }, Contents());
    }

    [Fact]
    public void Primary_SameItem_MergesUpToLimit_RestStaysOnCursor()
    {
        _inventory.Set(0, Stone(60));
        _inventory.Set(Cursor, Stone(10));

        Click(0, InventoryClickKind.Primary);

        Assert.Equal(new() { [0] = Stone(64), [Cursor] = Stone(6) }, Contents());
    }

    [Fact]
    public void Primary_SameItem_FullStack_NothingChanges()
    {
        _inventory.Set(0, Stone(64));
        _inventory.Set(Cursor, Stone(10));
        _inventory.TakeChanges();

        Click(0, InventoryClickKind.Primary);

        Assert.Equal(new() { [0] = Stone(64), [Cursor] = Stone(10) }, Contents());
        Assert.Empty(_inventory.TakeChanges());
    }

    // --- Клики ПКМ ---

    [Theory]
    [InlineData(5, 3, 2)]
    [InlineData(64, 32, 32)]
    [InlineData(1, 1, 0)]
    public void Secondary_EmptyCursor_TakesLargerHalf(int count, int taken, int left)
    {
        _inventory.Set(0, Stone(count));

        Click(0, InventoryClickKind.Secondary);

        Assert.Equal(Stone(taken), _inventory.Cursor);
        Assert.Equal(Stone(left), _inventory[0]);
    }

    [Fact]
    public void Secondary_PutsOneAtATime_LastOneEmptiesCursor()
    {
        _inventory.Set(Cursor, Stone(3));

        Click(4, InventoryClickKind.Secondary);
        Assert.Equal(new() { [4] = Stone(1), [Cursor] = Stone(2) }, Contents());

        Click(4, InventoryClickKind.Secondary);
        Click(4, InventoryClickKind.Secondary);
        Assert.Equal(new() { [4] = Stone(3) }, Contents());
    }

    [Fact]
    public void Secondary_OntoFullStack_NothingChanges()
    {
        _inventory.Set(0, Stone(64));
        _inventory.Set(Cursor, Stone(3));

        Click(0, InventoryClickKind.Secondary);

        Assert.Equal(new() { [0] = Stone(64), [Cursor] = Stone(3) }, Contents());
    }

    [Fact]
    public void Secondary_DifferentItem_Swaps()
    {
        _inventory.Set(0, Dirt(5));
        _inventory.Set(Cursor, Stone(3));

        Click(0, InventoryClickKind.Secondary);

        Assert.Equal(new() { [0] = Stone(3), [Cursor] = Dirt(5) }, Contents());
    }

    [Fact]
    public void AnyClick_EmptySlotAndCursor_NothingChanges()
    {
        foreach (var kind in Enum.GetValues<InventoryClickKind>())
            Click(0, kind);

        Assert.Empty(Contents());
        Assert.Empty(_inventory.TakeChanges());
    }

    // --- Shift+ЛКМ ---

    [Fact]
    public void QuickMove_FromHotbar_GoesToMainPart_MergingFirst()
    {
        _inventory.Set(0, Stone(10));
        _inventory.Set(15, Stone(60));

        Click(0, InventoryClickKind.QuickMove);

        Assert.Equal(new() { [15] = Stone(64), [Inventory.HotbarSize] = Stone(6) }, Contents());
    }

    [Fact]
    public void QuickMove_FromMainPart_GoesToHotbar()
    {
        _inventory.Set(0, Stone(10));
        _inventory.Set(20, Dirt(5));

        Click(20, InventoryClickKind.QuickMove);

        Assert.Equal(new() { [0] = Stone(10), [1] = Dirt(5) }, Contents());
    }

    [Fact]
    public void QuickMove_NotEnoughRoom_RestStaysInPlace()
    {
        Fill(0, Inventory.HotbarSize, Dirt(64));
        _inventory.Set(8, Stone(60));
        _inventory.Set(9, Stone(10));

        Click(9, InventoryClickKind.QuickMove);

        Assert.Equal(Stone(64), _inventory[8]);
        Assert.Equal(Stone(6), _inventory[9]);
    }

    // --- Неверные клики и изменения ---

    [Theory]
    [InlineData(-1, InventoryClickKind.Primary)]
    [InlineData(Inventory.CursorSlot, InventoryClickKind.Primary)]
    [InlineData(Inventory.SlotCount, InventoryClickKind.Secondary)]
    [InlineData(0, (InventoryClickKind)99)]
    public void InvalidClick_IsRejected_NothingChanges(int slot, InventoryClickKind kind)
    {
        _inventory.Set(0, Stone(10));
        _inventory.Set(Cursor, Dirt(3));
        _inventory.TakeChanges();

        Assert.False(_inventory.Click(slot, kind));

        Assert.Equal(new() { [0] = Stone(10), [Cursor] = Dirt(3) }, Contents());
        Assert.Empty(_inventory.TakeChanges());
    }

    [Fact]
    public void TakeChanges_ReportsChangedSlotsWithCursor_Once()
    {
        _inventory.Set(0, Stone(10));
        _inventory.Set(30, Dirt(1));
        _inventory.TakeChanges();

        Click(0, InventoryClickKind.Primary);

        Assert.Equal([new InventorySlot(0, ItemStack.Empty), new InventorySlot(Cursor, Stone(10))], _inventory.TakeChanges());
        Assert.Empty(_inventory.TakeChanges());
    }

    [Fact]
    public void TakeChanges_SameValueWritten_IsNotAChange()
    {
        _inventory.Set(0, Stone(10));
        _inventory.TakeChanges();

        _inventory.Set(0, Stone(10));

        Assert.Empty(_inventory.TakeChanges());
    }

    /// <summary>
    /// Случайные клики не создают и не теряют предметы и не превышают предел стопки. Ловит ошибки,
    /// до которых не дотянулись отдельные случаи: перепутанный слот, лишнее слагаемое, забытый остаток.
    /// </summary>
    [Fact]
    public void RandomClicks_ConserveItems_AndRespectLimits()
    {
        var random = new Random(12345);
        ItemId[] kinds = [_stone, _dirt, _pearl];
        for (var slot = 0; slot < Inventory.Size; slot++)
        {
            if (random.Next(3) == 0)
                continue;
            var item = kinds[random.Next(kinds.Length)];
            _inventory.Set(slot, new ItemStack(item, random.Next(1, _items.Get(item).MaxStackSize + 1)));
        }
        var expected = Totals();

        for (var i = 0; i < 5000; i++)
        {
            var kind = (InventoryClickKind)random.Next(3);
            var slot = random.Next(Inventory.Size);
            Click(slot, kind);

            Assert.Equal(expected, Totals());
            for (var s = 0; s < Inventory.SlotCount; s++)
            {
                var stack = _inventory[s];
                Assert.True(stack.IsEmpty || stack.Count <= _items.Get(stack.Item).MaxStackSize, $"Слот {s}: {stack} после клика {kind} по {slot}");
            }
        }
    }

    private Dictionary<ItemId, int> Totals()
    {
        var totals = new Dictionary<ItemId, int>();
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
        {
            var stack = _inventory[slot];
            if (!stack.IsEmpty)
                totals[stack.Item] = totals.GetValueOrDefault(stack.Item) + stack.Count;
        }
        return totals;
    }
}
