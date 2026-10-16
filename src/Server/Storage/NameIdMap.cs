namespace Server.Storage;

/// <summary>
/// Перевод числовых id (блоков или предметов) между реестром этого запуска и сохранением — по именам.
/// </summary>
/// <remarks>
/// <para>
/// Числовые id в реестре раздаются по порядку регистрации и могут поменяться, когда добавятся блоки, предметы или моды.
/// Поэтому у сохранения своя таблица «номер в файле → имя», и данные на диске хранятся в её номерах.
/// Таблица только растёт: новое имя получает следующий свободный номер, старые номера не меняются,
/// и уже записанные данные не нужно переписывать.
/// </para>
/// <para>
/// Номер 0 — «пусто» (воздух) и в реестре, и в сохранении. Имя из сохранения, которого нет в реестре (удалили мод),
/// при загрузке становится нулём: блок — воздухом, предмет — пустым слотом. Имя остаётся в таблице: если оно
/// вернётся, ещё не перезаписанные данные снова его покажут.
/// </para>
/// </remarks>
internal sealed class NameIdMap
{
    /// <summary>Номер в сохранении по id реестра.</summary>
    private readonly ushort[] _toSave;

    /// <summary>Id реестра по номеру в сохранении.</summary>
    private readonly ushort[] _toRuntime;

    /// <summary>Что переводится, для сообщений об ошибках: «блоков», «предметов».</summary>
    private readonly string _kind;

    /// <summary>
    /// Номера совпадают с id реестра (обычный случай: мир создан с тем же набором имён).
    /// Тогда перевод не меняет данных, и вызывающий может его пропустить.
    /// </summary>
    public bool IsIdentity { get; }

    /// <summary>Имена реестра, которых ещё нет в сохранении: их надо дописать в таблицу, по порядку номеров.</summary>
    public IReadOnlyList<string> NewNames { get; }

    /// <summary>Имена сохранения, которых нет в реестре: при загрузке они превращаются в 0.</summary>
    public IReadOnlyList<string> UnknownNames { get; }

    /// <param name="kind">Что переводится, в родительном падеже: «блоков», «предметов».</param>
    /// <param name="savedNames">Таблица сохранения: имя с номером i — на месте i. Для нового мира пустая.</param>
    /// <param name="runtimeNames">Имена реестра по порядку id; на месте 0 — «пусто» (воздух).</param>
    /// <exception cref="InvalidDataException">Таблица испорчена: повторы имён или номер 0 — не «пусто».</exception>
    public NameIdMap(string kind, IReadOnlyList<string> savedNames, IReadOnlyList<string> runtimeNames)
    {
        _kind = kind;
        var zeroName = runtimeNames[0];
        if (savedNames.Count > 0 && savedNames[0] != zeroName)
            throw new InvalidDataException($"В таблице {kind} сохранения номер 0 — '{savedNames[0]}', а должен быть '{zeroName}'.");
        if (savedNames.Count > ushort.MaxValue + 1)
            throw new InvalidDataException($"В таблице {kind} сохранения {savedNames.Count} имён, больше возможного.");

        var saveIdByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < savedNames.Count; i++)
        {
            if (!saveIdByName.TryAdd(savedNames[i], i))
                throw new InvalidDataException($"'{savedNames[i]}' дважды записан в таблице {kind} сохранения.");
        }

        var newNames = new List<string>();
        var nextSaveId = savedNames.Count;
        _toSave = new ushort[runtimeNames.Count];
        for (var id = 0; id < runtimeNames.Count; id++)
        {
            if (!saveIdByName.TryGetValue(runtimeNames[id], out var saveId))
            {
                if (nextSaveId > ushort.MaxValue)
                    throw new InvalidOperationException($"В таблице {kind} сохранения не осталось свободных номеров.");
                saveId = nextSaveId++;
                newNames.Add(runtimeNames[id]);
            }
            _toSave[id] = (ushort)saveId;
        }

        // Неизвестные имена остаются нулём: новый массив заполнен нулями.
        _toRuntime = new ushort[nextSaveId];
        for (var id = 0; id < runtimeNames.Count; id++)
            _toRuntime[_toSave[id]] = (ushort)id;

        NewNames = newNames;
        var known = runtimeNames.ToHashSet(StringComparer.Ordinal);
        UnknownNames = [.. savedNames.Where(name => !known.Contains(name))];

        var identity = UnknownNames.Count == 0;
        for (var i = 0; identity && i < _toSave.Length; i++)
            identity = _toSave[i] == i;
        IsIdentity = identity;
    }

    /// <summary>Номер в сохранении для id реестра.</summary>
    public ushort ToSave(ushort id) => _toSave[id];

    /// <summary>Переводит id из номеров реестра в номера сохранения.</summary>
    public void ToSave(ReadOnlySpan<ushort> source, Span<ushort> destination)
    {
        for (var i = 0; i < source.Length; i++)
            destination[i] = _toSave[source[i]];
    }

    /// <summary>Id реестра для номера из сохранения; 0, если имя неизвестно.</summary>
    /// <exception cref="InvalidDataException">Номера нет в таблице сохранения: данные испорчены.</exception>
    public ushort ToRuntime(int saveId)
    {
        if ((uint)saveId >= (uint)_toRuntime.Length)
            throw new InvalidDataException($"Номер {saveId} не записан в таблице {_kind} сохранения.");
        return _toRuntime[saveId];
    }

    /// <summary>Переводит прочитанные с диска номера сохранения в id реестра (на месте).</summary>
    /// <exception cref="InvalidDataException">Номера нет в таблице сохранения: данные испорчены.</exception>
    public void ToRuntime(Span<ushort> ids)
    {
        for (var i = 0; i < ids.Length; i++)
            ids[i] = ToRuntime(ids[i]);
    }
}
