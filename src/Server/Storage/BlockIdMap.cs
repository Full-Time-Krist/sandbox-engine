using System.Runtime.InteropServices;
using Shared.Blocks;

namespace Server.Storage;

/// <summary>
/// Перевод числовых id блоков между реестром этого запуска и сохранением (<see cref="NameIdMap"/>) для целых чанков.
/// Блок из сохранения, которого нет в реестре (удалили мод), при загрузке становится воздухом.
/// </summary>
internal sealed class BlockIdMap
{
    private readonly NameIdMap _map;

    /// <inheritdoc cref="NameIdMap.IsIdentity"/>
    public bool IsIdentity => _map.IsIdentity;

    /// <inheritdoc cref="NameIdMap.NewNames"/>
    public IReadOnlyList<string> NewNames => _map.NewNames;

    /// <inheritdoc cref="NameIdMap.UnknownNames"/>
    public IReadOnlyList<string> UnknownNames => _map.UnknownNames;

    /// <param name="savedNames">Таблица сохранения: имя блока с номером i — на месте i. Для нового мира пустая.</param>
    /// <exception cref="InvalidDataException">Таблица испорчена: повторы имён или номер 0 — не воздух.</exception>
    public BlockIdMap(IReadOnlyList<string> savedNames, BlockRegistry registry) =>
        _map = new NameIdMap("блоков", savedNames, [.. registry.All.Select(b => b.Name)]);

    // BlockId — обёртка над ushort, поэтому массив блоков можно читать как массив ushort без копирования.

    /// <summary>Переводит блоки из номеров реестра в номера сохранения.</summary>
    public void ToSave(ReadOnlySpan<BlockId> source, Span<BlockId> destination) =>
        _map.ToSave(MemoryMarshal.Cast<BlockId, ushort>(source), MemoryMarshal.Cast<BlockId, ushort>(destination));

    /// <summary>Переводит блоки, прочитанные с диска, из номеров сохранения в номера реестра (на месте).</summary>
    /// <exception cref="InvalidDataException">Номер, которого нет в таблице сохранения: данные испорчены.</exception>
    public void ToRuntime(Span<BlockId> blocks) => _map.ToRuntime(MemoryMarshal.Cast<BlockId, ushort>(blocks));
}
