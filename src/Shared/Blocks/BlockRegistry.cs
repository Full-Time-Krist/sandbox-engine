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
    /// <param name="light">Свет, который блок излучает сам, 0–<see cref="Lighting.ChunkLight.Max"/>.</param>
    /// <param name="needsSupport">Стоит только на твёрдом блоке (<see cref="BlockDefinition.NeedsSupport"/>).</param>
    /// <param name="shape">Форма; null — полный куб. Не-куб не может быть непрозрачным: он не закрывает грани соседей.</param>
    /// <param name="blocksLight">Свет в блок входит, но сквозь не проходит (<see cref="BlockDefinition.BlocksLight"/>).</param>
    public BlockDefinition Register(string name, bool isOpaque, bool isSolid = true, BlockMining? mining = null, int light = 0,
        bool needsSupport = false, BlockShape? shape = null, bool blocksLight = false)
    {
        if (IsFrozen)
            throw new InvalidOperationException($"Реестр заморожен, блок '{name}' зарегистрировать нельзя.");
        if (!ContentNames.IsValid(name))
            throw new ArgumentException($"Имя блока '{name}' должно иметь вид 'namespace:name' (a–z, 0–9, _).", nameof(name));
        if (_byName.ContainsKey(name))
            throw new InvalidOperationException($"Блок '{name}' уже зарегистрирован.");
        if ((uint)light > Lighting.ChunkLight.Max)
            throw new ArgumentOutOfRangeException(nameof(light), light, $"Свет блока '{name}' должен быть от 0 до {Lighting.ChunkLight.Max}.");
        if (shape is { IsFullCube: false } && isOpaque)
            throw new ArgumentException($"Блок '{name}' формы {shape} не может быть непрозрачным: он не закрывает грани соседей.", nameof(shape));
        if (_byId.Count > ushort.MaxValue)
            throw new InvalidOperationException($"Превышено число типов блоков ({ushort.MaxValue + 1}).");

        var definition = new BlockDefinition(new BlockId((ushort)_byId.Count), name, isOpaque, isSolid, mining ?? BlockMining.Default,
            (byte)light, needsSupport) { Shape = shape ?? BlockShape.Cube, BlocksLight = blocksLight };
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

    /// <summary>
    /// Может ли блок держать блок с опорой (<see cref="BlockDefinition.NeedsSupport"/>): твёрдый и с полным верхом
    /// (<see cref="BlockShape.HasFullTop"/>) — куб да, нижняя плита нет.
    /// </summary>
    public bool CanSupport(BlockId id) => IsSolid(id) && ShapeOf(id).HasFullTop;

    /// <summary>Форма блока. Id не из реестра — полный куб, как и в <see cref="IsSolid"/>.</summary>
    public BlockShape ShapeOf(BlockId id) => id.Value < _byId.Count ? _byId[id.Value].Shape : BlockShape.Cube;

    public bool TryGet(string name, out BlockDefinition definition) => _byName.TryGetValue(name, out definition!);
}
