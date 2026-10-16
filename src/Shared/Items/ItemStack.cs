namespace Shared.Items;

/// <summary>Стопка предметов в слоте: какой предмет и сколько штук.</summary>
/// <remarks>
/// Пустая стопка одна — <see cref="Empty"/> (воздух, 0 штук), она же значение по умолчанию: «камень ×0» и
/// «воздух ×5» конструктор превращает в неё. Иначе пустые слоты различались бы при сравнении.
/// Предел стопки стопка не знает (он в реестре предметов), его соблюдает <see cref="Inventory"/>.
/// </remarks>
public readonly record struct ItemStack
{
    public static readonly ItemStack Empty = default;

    public ItemId Item { get; }
    public int Count { get; }

    public ItemStack(ItemId item, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        (Item, Count) = item.IsAir || count == 0 ? (ItemId.Air, 0) : (item, count);
    }

    public bool IsEmpty => Count == 0;

    public override string ToString() => IsEmpty ? "пусто" : $"{Item} ×{Count}";
}

/// <summary>Содержимое одного слота инвентаря: для сообщений об изменённых слотах.</summary>
/// <param name="Slot">Номер слота; <see cref="Inventory.CursorSlot"/> — предмет на курсоре.</param>
public readonly record struct InventorySlot(int Slot, ItemStack Stack);
