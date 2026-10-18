namespace Shared.Blocks;

/// <summary>
/// Таблица типов блоков: строковое имя ↔ числовой <see cref="BlockId"/>.
/// </summary>
/// <remarks>
/// Блоки регистрируются при запуске, затем реестр замораживается (<see cref="Freeze"/>).
/// Числовые id раздаются по порядку регистрации, поэтому для сохранений и сети годятся только строковые имена.
/// </remarks>
public sealed class BlockRegistry
{
    public const string AirName = "base:air";

    private readonly List<BlockDefinition> _byId = [];
    private readonly Dictionary<string, BlockDefinition> _byName = new(StringComparer.Ordinal);

    public bool IsFrozen { get; private set; }
    public int Count => _byId.Count;
    public IReadOnlyList<BlockDefinition> All => _byId;

    public BlockRegistry()
    {
        // Воздух всегда получает id 0: новый массив блоков заполнен нулями, то есть воздухом.
        Register(AirName, isOpaque: false, isSolid: false, new BlockMining(0, null, 0));
    }

    /// <param name="isSolid">Тела в него упираются. По умолчанию да: таких блоков большинство.</param>
    /// <param name="mining">Как блок ломается; null — <see cref="BlockMining.Default"/>.</param>
    public BlockDefinition Register(string name, bool isOpaque, bool isSolid = true, BlockMining? mining = null)
    {
        if (IsFrozen)
            throw new InvalidOperationException($"Реестр заморожен, блок '{name}' зарегистрировать нельзя.");
        if (!ContentNames.IsValid(name))
            throw new ArgumentException($"Имя блока '{name}' должно иметь вид 'namespace:name' (a–z, 0–9, _).", nameof(name));
        if (_byName.ContainsKey(name))
            throw new InvalidOperationException($"Блок '{name}' уже зарегистрирован.");
        if (_byId.Count > ushort.MaxValue)
            throw new InvalidOperationException($"Превышено число типов блоков ({ushort.MaxValue + 1}).");

        var definition = new BlockDefinition(new BlockId((ushort)_byId.Count), name, isOpaque, isSolid, mining ?? BlockMining.Default);
        _byId.Add(definition);
        _byName.Add(name, definition);
        return definition;
    }

    public void Freeze() => IsFrozen = true;

    public BlockDefinition Get(BlockId id) =>
        id.Value < _byId.Count ? _byId[id.Value] : throw new KeyNotFoundException($"Блок {id} не зарегистрирован.");

    public BlockDefinition Get(string name) =>
        _byName.TryGetValue(name, out var definition) ? definition : throw new KeyNotFoundException($"Блок '{name}' не зарегистрирован.");

    /// <summary>
    /// Упираются ли тела в блок. Id не из реестра считается твёрдым: такой id может прийти только
    /// из испорченных данных, и лучше упереться в него, чем провалиться.
    /// </summary>
    public bool IsSolid(BlockId id) => id.Value >= _byId.Count || _byId[id.Value].IsSolid;

    public bool TryGet(string name, out BlockDefinition definition) => _byName.TryGetValue(name, out definition!);
}
