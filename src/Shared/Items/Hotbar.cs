using Shared.Blocks;

namespace Shared.Items;

/// <summary>
/// Панель быстрого доступа: слоты с предметами и выбранный слот. Авторитетная копия живёт на сервере.
/// </summary>
/// <remarks>
/// Пока предмет — это блок (<see cref="BlockId"/>), и набор слотов фиксирован. С инвентарём (этап 3)
/// и предметами из данных (этап 4) слоты будут хранить стопки предметов.
/// </remarks>
public sealed class Hotbar
{
    public const int Size = 6;

    private readonly BlockId[] _slots;

    public int SelectedSlot { get; private set; }
    public BlockId SelectedItem => _slots[SelectedSlot];

    public Hotbar(ReadOnlySpan<BlockId> items)
    {
        if (items.Length != Size)
            throw new ArgumentException($"В хотбаре {Size} слотов, передано {items.Length}.", nameof(items));
        _slots = items.ToArray();
    }

    public BlockId this[int slot] => _slots[slot];

    /// <summary>Выбирает слот. Номер вне диапазона игнорируется (он может прийти от клиента).</summary>
    public bool TrySelect(int slot)
    {
        if ((uint)slot >= Size)
            return false;
        SelectedSlot = slot;
        return true;
    }

    /// <summary>Стартовый набор: по одному виду каждого базового блока.</summary>
    public static Hotbar CreateDefault(BaseBlocks blocks) =>
        new([blocks.Stone, blocks.Dirt, blocks.Grass, blocks.Sand, blocks.Log, blocks.Planks]);
}
