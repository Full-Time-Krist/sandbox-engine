using Shared.Blocks;

namespace Shared.Items;

/// <summary>
/// Таблица типов предметов: строковое имя ↔ числовой <see cref="ItemId"/>. Отдельно от блоков:
/// инструменты, еда и оружие — предметы, но не блоки.
/// </summary>
/// <remarks>
/// Как и <see cref="BlockRegistry"/>: предметы регистрируются при запуске, затем реестр замораживается.
/// Числовые id раздаются по порядку регистрации, поэтому для сохранений и сети годятся только строковые имена.
/// </remarks>
public sealed class ItemRegistry
{
    /// <summary>Пустой слот — предмет «воздух», одноимённый блоку-воздуху.</summary>
    public const string AirName = BlockRegistry.AirName;

    /// <summary>Поведение по умолчанию: пустая рука (ломает блок). Его же получает пустой слот.</summary>
    public const string HandBehavior = "base:hand";

    /// <summary>Поведение по умолчанию для предмета, который ставит блок.</summary>
    public const string PlaceBlockBehavior = "base:place_block";

    private readonly List<ItemDefinition> _byId = [];
    private readonly Dictionary<string, ItemDefinition> _byName = new(StringComparer.Ordinal);

    /// <summary>Какой предмет получается из сломанного блока.</summary>
    private readonly Dictionary<BlockId, ItemId> _byBlock = [];

    public bool IsFrozen { get; private set; }
    public int Count => _byId.Count;
    public IReadOnlyList<ItemDefinition> All => _byId;

    public ItemRegistry()
    {
        // Воздух всегда получает id 0: новый массив стопок заполнен нулями, то есть пустыми слотами.
        // Ничего не ставит и ни во что не складывается.
        Add(new ItemDefinition(ItemId.Air, AirName, MaxStackSize: 0, Block: null, HandBehavior));
    }

    /// <param name="block">Блок, который предмет ставит в мир. Первый предмет, зарегистрированный для блока,
    /// становится и добычей с него.</param>
    /// <param name="behavior">Имя поведения; null — по умолчанию (<see cref="PlaceBlockBehavior"/> для предмета-блока,
    /// иначе <see cref="HandBehavior"/>).</param>
    public ItemDefinition Register(string name, int maxStackSize, BlockId? block = null, string? behavior = null)
    {
        if (IsFrozen)
            throw new InvalidOperationException($"Реестр заморожен, предмет '{name}' зарегистрировать нельзя.");
        if (!ContentNames.IsValid(name))
            throw new ArgumentException($"Имя предмета '{name}' должно иметь вид 'namespace:name' (a–z, 0–9, _).", nameof(name));
        if (_byName.ContainsKey(name))
            throw new InvalidOperationException($"Предмет '{name}' уже зарегистрирован.");
        if (_byId.Count > ushort.MaxValue)
            throw new InvalidOperationException($"Превышено число типов предметов ({ushort.MaxValue + 1}).");
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStackSize, 1);
        if (block is { IsAir: true })
            throw new ArgumentException($"Предмет '{name}' не может ставить воздух.", nameof(block));

        if (behavior != null && !ContentNames.IsValid(behavior))
            throw new ArgumentException($"Имя поведения '{behavior}' должно иметь вид 'namespace:name'.", nameof(behavior));

        var definition = new ItemDefinition(new ItemId((ushort)_byId.Count), name, maxStackSize, block,
            behavior ?? (block != null ? PlaceBlockBehavior : HandBehavior));
        Add(definition);
        if (block is { } placed)
            _byBlock.TryAdd(placed, definition.Id);
        return definition;
    }

    private void Add(ItemDefinition definition)
    {
        _byId.Add(definition);
        _byName.Add(definition.Name, definition);
    }

    public void Freeze() => IsFrozen = true;

    public bool Contains(ItemId id) => id.Value < _byId.Count;

    public ItemDefinition Get(ItemId id) =>
        Contains(id) ? _byId[id.Value] : throw new KeyNotFoundException($"Предмет {id} не зарегистрирован.");

    public ItemDefinition Get(string name) =>
        _byName.TryGetValue(name, out var definition) ? definition : throw new KeyNotFoundException($"Предмет '{name}' не зарегистрирован.");

    public bool TryGet(string name, out ItemDefinition definition) => _byName.TryGetValue(name, out definition!);

    /// <summary>Предмет, который даёт сломанный блок, или воздух (ничего), если блок предметом не становится.</summary>
    public ItemId ForBlock(BlockId block) => _byBlock.GetValueOrDefault(block);
}
