using System.Collections.Immutable;
using System.Numerics;

namespace Shared.Items;

/// <summary>
/// Инвентарь игрока: 36 слотов (9 хотбара и 27 основных), выбранный слот хотбара и предмет на курсоре
/// (взятый мышью на экране инвентаря).
/// </summary>
/// <remarks>
/// Авторитетная копия живёт на сервере. Клиент держит свою для отображения и шлёт только намерения (выбор слота,
/// клики), а сервер применяет их этим же кодом и присылает изменённые слоты (<see cref="TakeChanges"/>).
/// Так клиент не может «нарисовать» себе предметы.
/// <para>Все изменения слотов идут через <see cref="Put"/>: он запоминает изменённые слоты для отправки.</para>
/// </remarks>
public sealed class Inventory
{
    public const int HotbarSize = 9;
    public const int MainSize = 27;

    /// <summary>Число слотов: 0–8 — хотбар, 9–35 — основная часть.</summary>
    public const int Size = HotbarSize + MainSize;

    /// <summary>Номер предмета на курсоре: сразу за последним слотом. Кликнуть по нему нельзя, но он меняется и передаётся как слот.</summary>
    public const int CursorSlot = Size;

    /// <summary>Слоты вместе с курсором. Изменённые хранятся битами в <see cref="ulong"/>, поэтому их не больше 64.</summary>
    public const int SlotCount = Size + 1;

    private readonly ItemRegistry _items;
    private readonly ItemStack[] _slots = new ItemStack[SlotCount];

    /// <summary>Слоты, изменённые после прошлого <see cref="TakeChanges"/>: бит на слот.</summary>
    private ulong _changed;

    public Inventory(ItemRegistry items) => _items = items;

    /// <summary>Выбранный слот хотбара: предмет в руке.</summary>
    public int SelectedSlot { get; private set; }

    public ItemStack Selected => _slots[SelectedSlot];
    public ItemStack Cursor => _slots[CursorSlot];

    /// <param name="slot">От 0 до <see cref="CursorSlot"/> включительно.</param>
    public ItemStack this[int slot] => _slots[slot];

    /// <summary>Выбирает слот хотбара. Номер вне хотбара игнорируется (он может прийти от клиента).</summary>
    public bool TrySelect(int slot)
    {
        if ((uint)slot >= HotbarSize)
            return false;
        SelectedSlot = slot;
        return true;
    }

    /// <summary>
    /// Кладёт стопку: сначала доливает неполные стопки того же предмета, потом занимает первые пустые слоты;
    /// и там и там хотбар раньше основной части. Возвращает, сколько штук не поместилось.
    /// </summary>
    public int Add(ItemStack stack) => AddTo(stack, 0, Size);

    /// <summary>Поместится ли стопка целиком (в неполные стопки того же предмета и пустые слоты).</summary>
    public bool CanAdd(ItemStack stack)
    {
        if (stack.IsEmpty)
            return true;

        var max = MaxStack(stack.Item);
        var room = 0;
        for (var slot = 0; slot < Size && room < stack.Count; slot++)
        {
            var current = _slots[slot];
            if (current.IsEmpty)
                room += max;
            else if (current.Item == stack.Item)
                room += max - current.Count;
        }
        return room >= stack.Count;
    }

    /// <summary>
    /// Кладёт предмет с курсора в слоты, как добычу (<see cref="Add"/>). Что не поместилось, остаётся на курсоре:
    /// так ничего не теряется. Для загрузки сохранения, позже — для закрытия экрана инвентаря.
    /// </summary>
    public void ReturnCursor()
    {
        var cursor = Cursor;
        Put(CursorSlot, new ItemStack(cursor.Item, Add(cursor)));
    }

    /// <summary>Сколько штук предмета во всех слотах (курсор не считается).</summary>
    public int Count(ItemId item)
    {
        if (item.IsAir)
            return 0;
        var count = 0;
        for (var slot = 0; slot < Size; slot++)
        {
            if (_slots[slot].Item == item)
                count += _slots[slot].Count;
        }
        return count;
    }

    /// <summary>
    /// Обмен целиком или никак: забирает стопки <paramref name="take"/> из слотов (сначала из основной части, потом из
    /// хотбара — чтобы не трогать то, что под рукой) и кладёт <paramref name="give"/>, как добычу (<see cref="Add"/>).
    /// Если чего-то не хватает или результату не хватает места даже после траты, ничего не меняется и возвращается false.
    /// Место считается после траты: последние доски, ушедшие на верстак, освобождают для него слот.
    /// </summary>
    public bool TryExchange(IReadOnlyList<ItemStack> take, ItemStack give)
    {
        ArgumentNullException.ThrowIfNull(take);
        var saved = (ItemStack[])_slots.Clone();
        var savedChanged = _changed;

        var ok = true;
        foreach (var stack in take)
        {
            var left = TakeFrom(stack.Item, stack.Count, HotbarSize, Size);
            left = TakeFrom(stack.Item, left, 0, HotbarSize);
            if (left > 0)
            {
                ok = false;
                break;
            }
        }

        if (ok && AddTo(give, 0, Size) == 0)
            return true;

        // Откат: слоты и отметки об изменениях — как до обмена, клиенту нечего присылать.
        Array.Copy(saved, _slots, SlotCount);
        _changed = savedChanged;
        return false;
    }

    /// <summary>Забирает до <paramref name="count"/> штук предмета из слотов [from, to). Возвращает, сколько не нашлось.</summary>
    private int TakeFrom(ItemId item, int count, int from, int to)
    {
        for (var slot = from; slot < to && count > 0; slot++)
        {
            var current = _slots[slot];
            if (current.IsEmpty || current.Item != item)
                continue;
            var taken = Math.Min(count, current.Count);
            Put(slot, new ItemStack(item, current.Count - taken));
            count -= taken;
        }
        return count;
    }

    /// <summary>Убирает <paramref name="count"/> штук из слота. Последняя штука освобождает слот.</summary>
    public void Remove(int slot, int count)
    {
        var stack = _slots[slot];
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, stack.Count);
        Put(slot, new ItemStack(stack.Item, stack.Count - count));
    }

    /// <summary>
    /// Заменяет содержимое слота (или курсора). Для клиента — применить присланное сервером; для сервера —
    /// выдать стопку целиком (стартовый набор, позже творческий режим и загрузка сохранения).
    /// </summary>
    /// <exception cref="ArgumentException">Предмет не зарегистрирован или стопка больше предела.</exception>
    public void Set(int slot, ItemStack stack)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, SlotCount);
        if (!stack.IsEmpty && (!_items.Contains(stack.Item) || stack.Count > MaxStack(stack.Item)))
            throw new ArgumentException($"Стопка {stack} не помещается в слот: предмет не зарегистрирован или штук больше предела.", nameof(stack));
        Put(slot, stack);
    }

    /// <summary>
    /// Клик по слоту на экране инвентаря. Возвращает false, если клик неверный (номер вне 0–35 или неизвестный вид):
    /// он может прийти от клиента, и сервер должен его отклонить, а не упасть.
    /// </summary>
    public bool Click(int slot, InventoryClickKind kind)
    {
        if ((uint)slot >= Size)
            return false;

        switch (kind)
        {
            case InventoryClickKind.Primary:
                ClickPrimary(slot);
                return true;
            case InventoryClickKind.Secondary:
                ClickSecondary(slot);
                return true;
            case InventoryClickKind.QuickMove:
                QuickMove(slot);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Все слоты и курсор по порядку номеров: для полного инвентаря в сообщении.</summary>
    public ImmutableArray<ItemStack> CopySlots() => [.. _slots];

    /// <summary>Изменённые слоты с прошлого вызова (по возрастанию номеров); после вызова список изменений пуст.</summary>
    public ImmutableArray<InventorySlot> TakeChanges()
    {
        if (_changed == 0)
            return [];

        var changes = ImmutableArray.CreateBuilder<InventorySlot>(BitOperations.PopCount(_changed));
        for (var slot = 0; slot < SlotCount; slot++)
        {
            if ((_changed & (1UL << slot)) != 0)
                changes.Add(new InventorySlot(slot, _slots[slot]));
        }
        _changed = 0;
        return changes.MoveToImmutable();
    }

    /// <summary>ЛКМ: с пустым курсором — взять стопку, на пустой слот — положить, тот же предмет — доложить, другой — поменять.</summary>
    private void ClickPrimary(int slot)
    {
        var held = Cursor;
        var stack = _slots[slot];
        if (!held.IsEmpty && held.Item == stack.Item)
        {
            // Доложить сколько влезет, остаток остаётся на курсоре. Полная стопка ничего не принимает.
            var moved = Math.Min(held.Count, MaxStack(stack.Item) - stack.Count);
            Put(slot, new ItemStack(stack.Item, stack.Count + moved));
            Put(CursorSlot, new ItemStack(held.Item, held.Count - moved));
        }
        else
        {
            // Взять (курсор пуст), положить (слот пуст) или поменять местами: всё это — обмен.
            Put(slot, held);
            Put(CursorSlot, stack);
        }
    }

    /// <summary>ПКМ: с пустым курсором — взять половину, иначе положить одну штуку; другой предмет — поменять.</summary>
    private void ClickSecondary(int slot)
    {
        var held = Cursor;
        var stack = _slots[slot];
        if (held.IsEmpty)
        {
            // Большая половина — на курсор: из 5 штук берётся 3, из одной — она сама.
            var taken = (stack.Count + 1) / 2;
            Put(CursorSlot, new ItemStack(stack.Item, taken));
            Put(slot, new ItemStack(stack.Item, stack.Count - taken));
        }
        else if (stack.IsEmpty || stack.Item == held.Item)
        {
            if (stack.Count >= MaxStack(held.Item))
                return;
            Put(slot, new ItemStack(held.Item, stack.Count + 1));
            Put(CursorSlot, new ItemStack(held.Item, held.Count - 1));
        }
        else
        {
            Put(slot, held);
            Put(CursorSlot, stack);
        }
    }

    /// <summary>Shift+ЛКМ: стопка из хотбара уходит в основную часть, из основной — в хотбар. Что не влезло, остаётся.</summary>
    private void QuickMove(int slot)
    {
        var stack = _slots[slot];
        if (stack.IsEmpty)
            return;

        var (from, to) = slot < HotbarSize ? (HotbarSize, Size) : (0, HotbarSize);
        // Слот-источник вне диапазона назначения, поэтому стопка не попадёт обратно в него же.
        var left = AddTo(stack, from, to);
        Put(slot, new ItemStack(stack.Item, left));
    }

    /// <summary>Кладёт стопку в слоты [from, to): сначала в неполные того же предмета, потом в пустые. Возвращает остаток.</summary>
    private int AddTo(ItemStack stack, int from, int to)
    {
        if (stack.IsEmpty)
            return 0;

        var max = MaxStack(stack.Item);
        var left = stack.Count;

        // Сначала неполные стопки: иначе каждая добыча занимала бы новый слот, пока рядом есть место.
        for (var slot = from; slot < to && left > 0; slot++)
        {
            var current = _slots[slot];
            if (current.Item == stack.Item && current.Count < max)
            {
                var moved = Math.Min(left, max - current.Count);
                Put(slot, new ItemStack(stack.Item, current.Count + moved));
                left -= moved;
            }
        }

        for (var slot = from; slot < to && left > 0; slot++)
        {
            if (_slots[slot].IsEmpty)
            {
                var moved = Math.Min(left, max);
                Put(slot, new ItemStack(stack.Item, moved));
                left -= moved;
            }
        }

        return left;
    }

    /// <summary>
    /// Распределяет стопку с курсора по выбранным слотам перетаскиванием.
    /// ЛКМ делит стопку максимально равномерно; ПКМ кладёт по одной штуке в каждый подходящий слот.
    /// Несовместимые и полные слоты пропускаются, остаток остаётся на курсоре.
    /// </summary>
    public bool DistributeCursor(IReadOnlyList<int> targetSlots, bool onePerSlot)
    {
        ArgumentNullException.ThrowIfNull(targetSlots);
        if (targetSlots.Count > Size)
            return false;

        var seen = new HashSet<int>();
        foreach (var slot in targetSlots)
        {
            if ((uint)slot >= Size || !seen.Add(slot))
                return false;
        }

        var held = Cursor;
        if (held.IsEmpty || targetSlots.Count == 0)
            return true;

        var max = MaxStack(held.Item);
        var targets = targetSlots
            .Where(slot => (_slots[slot].IsEmpty || _slots[slot].Item == held.Item)
                           && _slots[slot].Count < max)
            .ToArray();
        var remaining = held.Count;

        if (onePerSlot)
        {
            foreach (var slot in targets)
            {
                if (remaining == 0)
                    break;
                Put(slot, new ItemStack(held.Item, _slots[slot].Count + 1));
                remaining--;
            }
        }
        else
        {
            // Делим поровну между всеми слотами, перераспределяя остаток, если один из них заполнится.
            var active = targets.ToList();
            while (remaining > 0 && active.Count > 0)
            {
                var quotient = remaining / active.Count;
                var extra = remaining % active.Count;
                var movedThisPass = 0;

                for (var i = 0; i < active.Count && remaining > 0; i++)
                {
                    var slot = active[i];
                    var current = _slots[slot];
                    var capacity = max - current.Count;
                    var desired = quotient + (i < extra ? 1 : 0);
                    if (desired == 0)
                        continue;

                    var moved = Math.Min(Math.Min(desired, capacity), remaining);
                    if (moved == 0)
                        continue;

                    Put(slot, new ItemStack(held.Item, current.Count + moved));
                    remaining -= moved;
                    movedThisPass += moved;
                }

                active.RemoveAll(slot => _slots[slot].Count >= max);
                if (movedThisPass == 0)
                    break;
            }
        }

        Put(CursorSlot, new ItemStack(held.Item, remaining));
        return true;
    }

    private int MaxStack(ItemId item) => _items.Get(item).MaxStackSize;

    /// <summary>Единственное место, где меняются слоты: запоминает изменённые для <see cref="TakeChanges"/>.</summary>
    private void Put(int slot, ItemStack stack)
    {
        if (_slots[slot] == stack)
            return;
        _slots[slot] = stack;
        _changed |= 1UL << slot;
    }
}
