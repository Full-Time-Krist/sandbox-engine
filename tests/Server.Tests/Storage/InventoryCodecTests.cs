using Server.Storage;
using Shared.Items;

namespace Server.Tests.Storage;

public class InventoryCodecTests
{
    private readonly ItemRegistry _items;
    private readonly NameIdMap _identity;

    public InventoryCodecTests()
    {
        _items = WorldSaveTests.ItemsFor(BlockIdMapTests.Registry("base:stone", "base:dirt"));
        _identity = new NameIdMap("предметов", [], [.. _items.All.Select(i => i.Name)]);
    }

    private ItemStack Stone(int count) => new(_items.Get("base:stone").Id, count);
    private ItemStack Dirt(int count) => new(_items.Get("base:dirt").Id, count);

    private ItemStack[] Decode(params byte[] data) => InventoryCodec.Decode(data, _identity, _items);

    [Fact]
    public void RoundTrip_KeepsEverySlotAndCursor()
    {
        var slots = new ItemStack[Inventory.SlotCount];
        slots[0] = Stone(64);
        slots[17] = Dirt(1);
        slots[Inventory.Size - 1] = Stone(44);
        slots[Inventory.CursorSlot] = Dirt(33);

        var data = InventoryCodec.Encode(slots, _identity);

        Assert.Equal(slots, InventoryCodec.Decode(data, _identity, _items));
        Assert.Equal(InventoryCodec.Version, data[0]);
    }

    [Fact]
    public void EmptyInventory_IsTwoBytes()
    {
        var data = InventoryCodec.Encode(new ItemStack[Inventory.SlotCount], _identity);

        Assert.Equal([InventoryCodec.Version, 0], data);
        Assert.All(Decode(data), s => Assert.True(s.IsEmpty));
    }

    [Fact]
    public void ItemsAreStoredInSaveNumbers()
    {
        // В сохранении земля — 1, камень — 2; в реестре наоборот.
        var map = new NameIdMap("предметов", ["base:air", "base:dirt", "base:stone"], [.. _items.All.Select(i => i.Name)]);
        var slots = new ItemStack[Inventory.SlotCount];
        slots[0] = Stone(5);

        var data = InventoryCodec.Encode(slots, map);

        Assert.Equal([InventoryCodec.Version, 1, 0, 2, 5], data); // одна стопка: слот 0, номер 2, 5 штук
        Assert.Equal(Stone(5), InventoryCodec.Decode(data, map, _items)[0]);
    }

    [Fact]
    public void StackAboveLimit_IsCutToLimit()
    {
        // Предел стопки мог уменьшиться с тех пор, как инвентарь записали: стопка урезается, а не ломает загрузку.
        var stone = (byte)_items.Get("base:stone").Id.Value;

        Assert.Equal(Stone(64), Decode(InventoryCodec.Version, 1, 3, stone, 100)[3]);
    }

    [Theory]
    [InlineData(new byte[0])]                               // нет данных
    [InlineData(new byte[] { 2, 0 })]                       // неизвестная версия
    [InlineData(new byte[] { 1 })]                          // нет числа стопок
    [InlineData(new byte[] { 1, 1, 0, 1 })]                 // стопка оборвана
    [InlineData(new byte[] { 1, 38, 0, 1, 1 })]             // стопок больше, чем слотов
    [InlineData(new byte[] { 1, 1, 37, 1, 1 })]             // слот за курсором
    [InlineData(new byte[] { 1, 2, 5, 1, 1, 5, 1, 1 })]     // один слот дважды
    [InlineData(new byte[] { 1, 1, 0, 1, 0 })]              // пустая стопка
    [InlineData(new byte[] { 1, 1, 0, 9, 1 })]              // номера предмета нет в таблице
    [InlineData(new byte[] { 1, 0, 7 })]                    // лишние данные
    [InlineData(new byte[] { 1, 1, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F })] // число больше int
    public void CorruptData_IsInvalid(byte[] data)
    {
        Assert.Throws<InvalidDataException>(() => Decode(data));
    }
}
