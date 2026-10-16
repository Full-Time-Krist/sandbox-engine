using Shared.Items;

namespace Server.Storage;

/// <summary>
/// Инвентарь игрока в сохранении — двоичный блок: байт версии формата, число непустых слотов и для каждого — номер
/// слота, номер предмета в сохранении (<see cref="NameIdMap"/>) и число штук. Числа — переменной длины (по 7 бит
/// в байте): обычный слот занимает 3 байта.
/// </summary>
/// <remarks>
/// Данные с диска не считаются надёжными: <see cref="Decode"/> проверяет версию, номера слотов и предметов
/// и при ошибке бросает <see cref="InvalidDataException"/>.
/// </remarks>
internal static class InventoryCodec
{
    public const byte Version = 1;

    /// <param name="slots">Все слоты и курсор (<see cref="Inventory.SlotCount"/>) в id реестра.</param>
    public static byte[] Encode(ReadOnlySpan<ItemStack> slots, NameIdMap items)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Version);
            var count = 0;
            foreach (var stack in slots)
                count += stack.IsEmpty ? 0 : 1;
            writer.Write7BitEncodedInt(count);

            for (var slot = 0; slot < slots.Length; slot++)
            {
                var stack = slots[slot];
                if (stack.IsEmpty)
                    continue;
                writer.Write7BitEncodedInt(slot);
                writer.Write7BitEncodedInt(items.ToSave(stack.Item.Value));
                writer.Write7BitEncodedInt(stack.Count);
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Читает слоты (<see cref="Inventory.SlotCount"/> штук, в id реестра). Предмет, которого больше нет в реестре,
    /// пропадает (его имя сообщается при открытии сохранения); стопка больше предела урезается до него с записью в лог.
    /// </summary>
    /// <exception cref="InvalidDataException">Данные испорчены или от неизвестной версии формата.</exception>
    public static ItemStack[] Decode(ReadOnlySpan<byte> data, NameIdMap items, ItemRegistry registry)
    {
        if (data.Length == 0 || data[0] != Version)
            throw new InvalidDataException($"Неизвестная версия формата инвентаря: {(data.Length == 0 ? "нет данных" : data[0])}.");

        var slots = new ItemStack[Inventory.SlotCount];
        var reader = new Reader(data[1..]);
        var count = reader.ReadInt();
        if (count > Inventory.SlotCount)
            throw new InvalidDataException($"В инвентаре {count} стопок, слотов всего {Inventory.SlotCount}.");

        var seen = new bool[Inventory.SlotCount];
        for (var i = 0; i < count; i++)
        {
            var slot = reader.ReadInt();
            var saveId = reader.ReadInt();
            var amount = reader.ReadInt();
            if (slot >= Inventory.SlotCount || seen[slot])
                throw new InvalidDataException($"Неверный или повторный номер слота {slot}.");
            if (amount == 0)
                throw new InvalidDataException($"Пустая стопка в слоте {slot}.");
            seen[slot] = true;

            var item = new ItemId(items.ToRuntime(saveId));
            if (item.IsAir)
                continue;

            var max = registry.Get(item).MaxStackSize;
            if (amount > max)
            {
                Console.WriteLine($"[Server] В слоте {slot} {amount} × {registry.Get(item).Name}, больше предела {max}: лишнее пропадёт");
                amount = max;
            }
            slots[slot] = new ItemStack(item, amount);
        }

        if (!reader.AtEnd)
            throw new InvalidDataException("Лишние данные после инвентаря.");
        return slots;
    }

    /// <summary>Чтение чисел переменной длины из куска памяти: как <see cref="BinaryReader.Read7BitEncodedInt"/>, но без потока.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly bool AtEnd => _position == _data.Length;

        /// <summary>Неотрицательное число до <see cref="int.MaxValue"/>: не больше 5 байт.</summary>
        public int ReadInt()
        {
            var value = 0L;
            for (var shift = 0; shift < 35; shift += 7)
            {
                if (_position >= _data.Length)
                    throw new InvalidDataException("Данные инвентаря обрываются.");
                var b = _data[_position++];
                value |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (value > int.MaxValue)
                        break;
                    return (int)value;
                }
            }
            throw new InvalidDataException("Неверное число в данных инвентаря.");
        }
    }
}
