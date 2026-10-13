using Shared.Blocks;

namespace Server.Storage;

/// <summary>
/// Перевод числовых id блоков между реестром этого запуска и сохранением.
/// </summary>
/// <remarks>
/// <para>
/// Числовые id в реестре раздаются по порядку регистрации и могут поменяться, когда добавятся блоки или моды.
/// Поэтому у сохранения своя таблица «id в файле → имя блока», и чанки на диске хранятся в её номерах.
/// Таблица только растёт: новый блок получает следующий свободный номер, старые номера не меняются,
/// и уже записанные чанки не нужно переписывать.
/// </para>
/// <para>
/// Блок из сохранения, которого нет в реестре (удалили мод), при загрузке становится воздухом. Имя остаётся
/// в таблице: если блок вернётся, ещё не перезаписанные чанки снова его покажут.
/// </para>
/// </remarks>
internal sealed class BlockIdMap
{
    /// <summary>Номер в сохранении по id реестра.</summary>
    private readonly BlockId[] _toSave;

    /// <summary>Id реестра по номеру в сохранении.</summary>
    private readonly BlockId[] _toRuntime;

    /// <summary>
    /// Номера совпадают с id реестра (обычный случай: мир создан с тем же набором блоков).
    /// Тогда перевод не нужен, и чанки пишутся и читаются без лишнего прохода по блокам.
    /// </summary>
    public bool IsIdentity { get; }

    /// <summary>Блоки реестра, которых ещё нет в сохранении: их надо дописать в таблицу, по порядку номеров.</summary>
    public IReadOnlyList<string> NewNames { get; }

    /// <summary>Блоки сохранения, которых нет в реестре: при загрузке они превращаются в воздух.</summary>
    public IReadOnlyList<string> UnknownNames { get; }

    /// <param name="savedNames">Таблица сохранения: имя блока с номером i — на месте i. Для нового мира пустая.</param>
    /// <exception cref="InvalidDataException">Таблица испорчена: повторы имён или номер 0 — не воздух.</exception>
    public BlockIdMap(IReadOnlyList<string> savedNames, BlockRegistry registry)
    {
        // Воздух — 0 и в реестре, и в сохранении: пустые чанки и новые массивы блоков одинаковы в обоих номерах.
        if (savedNames.Count > 0 && savedNames[0] != BlockRegistry.AirName)
            throw new InvalidDataException($"В таблице блоков сохранения номер 0 — '{savedNames[0]}', а должен быть воздух.");
        if (savedNames.Count > ushort.MaxValue + 1)
            throw new InvalidDataException($"В таблице блоков сохранения {savedNames.Count} имён, больше возможного.");

        var saveIdByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < savedNames.Count; i++)
        {
            if (!saveIdByName.TryAdd(savedNames[i], i))
                throw new InvalidDataException($"Блок '{savedNames[i]}' дважды записан в таблице блоков сохранения.");
        }

        var newNames = new List<string>();
        var nextSaveId = savedNames.Count;
        _toSave = new BlockId[registry.Count];
        foreach (var block in registry.All)
        {
            if (!saveIdByName.TryGetValue(block.Name, out var saveId))
            {
                if (nextSaveId > ushort.MaxValue)
                    throw new InvalidOperationException("В таблице блоков сохранения не осталось свободных номеров.");
                saveId = nextSaveId++;
                newNames.Add(block.Name);
            }
            _toSave[block.Id.Value] = new BlockId((ushort)saveId);
        }

        // Неизвестные имена остаются воздухом: новый массив заполнен нулями.
        _toRuntime = new BlockId[nextSaveId];
        foreach (var block in registry.All)
            _toRuntime[_toSave[block.Id.Value].Value] = block.Id;

        NewNames = newNames;
        UnknownNames = [.. savedNames.Where(name => !registry.TryGet(name, out _))];

        var identity = UnknownNames.Count == 0;
        for (var i = 0; identity && i < _toSave.Length; i++)
            identity = _toSave[i].Value == i;
        IsIdentity = identity;
    }

    /// <summary>Переводит блоки из номеров реестра в номера сохранения.</summary>
    public void ToSave(ReadOnlySpan<BlockId> source, Span<BlockId> destination)
    {
        for (var i = 0; i < source.Length; i++)
            destination[i] = _toSave[source[i].Value];
    }

    /// <summary>Переводит блоки, прочитанные с диска, из номеров сохранения в номера реестра (на месте).</summary>
    /// <exception cref="InvalidDataException">Номер, которого нет в таблице сохранения: данные испорчены.</exception>
    public void ToRuntime(Span<BlockId> blocks)
    {
        for (var i = 0; i < blocks.Length; i++)
        {
            var saveId = blocks[i].Value;
            if (saveId >= _toRuntime.Length)
                throw new InvalidDataException($"Блок с номером {saveId} не записан в таблице блоков сохранения.");
            blocks[i] = _toRuntime[saveId];
        }
    }
}
