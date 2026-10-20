using System.Collections.Immutable;
using Shared.Items;

namespace Shared.Mobs;

/// <summary>
/// Числовой идентификатор вида моба. Раздаётся реестром при запуске и действует только в текущем сеансе; постоянное
/// имя — строковое (<see cref="MobDefinition.Name"/>). Id 0 — «не моб» (у предмета на земле, у предмета-не-яйца).
/// </summary>
public readonly record struct MobId(ushort Value)
{
    public static readonly MobId None = new(0);

    public bool IsNone => Value == 0;

    public override string ToString() => $"#{Value}";
}

/// <summary>Добыча с моба: от <paramref name="Min"/> до <paramref name="Max"/> штук предмета (включительно).</summary>
public readonly record struct MobDrop(ItemId Item, int Min, int Max);

/// <summary>Вид моба (<c>type: "mob"</c> в JSON): всё, что нужно серверу. Вид на клиенте описывает модель (шаг 9.2).</summary>
/// <param name="Name">Постоянное имя вида <c>namespace:name</c>.</param>
/// <param name="Health">Здоровье, в тех же единицах, что у игрока (у игрока 20).</param>
/// <param name="Speed">Скорость ходьбы, блоков в секунду (игрок — 4,3).</param>
/// <param name="Width">Ширина короба тела по X и Z, в блоках.</param>
/// <param name="Height">Высота короба тела, в блоках.</param>
/// <param name="Behavior">Поведение по имени (<c>base:hostile_melee</c>, <c>base:passive</c>) — как у предметов.</param>
/// <param name="Drops">Что роняет, когда погибнет.</param>
public sealed record MobDefinition(MobId Id, string Name, int Health, float Speed, double Width, double Height, string Behavior,
    ImmutableArray<MobDrop> Drops);

/// <summary>Таблица видов мобов: имя ↔ <see cref="MobId"/>. Как реестры блоков и предметов: заполняется при запуске и замораживается.</summary>
/// <remarks>Id раздаются по порядку регистрации начиная с 1, поэтому для сохранений и сети годятся только имена.</remarks>
public sealed class MobRegistry
{
    private readonly List<MobDefinition> _byId = [];
    private readonly Dictionary<string, MobDefinition> _byName = new(StringComparer.Ordinal);

    public bool IsFrozen { get; private set; }
    public int Count => _byId.Count;
    public IReadOnlyList<MobDefinition> All => _byId;

    /// <summary>Id, который получит следующий зарегистрированный вид.</summary>
    public MobId NextId => new((ushort)(_byId.Count + 1));

    public MobDefinition Register(string name, int health, float speed, double width, double height, string behavior,
        ImmutableArray<MobDrop> drops = default)
    {
        if (IsFrozen)
            throw new InvalidOperationException($"Реестр заморожен, моба '{name}' зарегистрировать нельзя.");
        if (!ContentNames.IsValid(name))
            throw new ArgumentException($"Имя моба '{name}' должно иметь вид 'namespace:name' (a–z, 0–9, _).", nameof(name));
        if (_byName.ContainsKey(name))
            throw new InvalidOperationException($"Моб '{name}' уже зарегистрирован.");
        if (_byId.Count >= ushort.MaxValue)
            throw new InvalidOperationException($"Превышено число видов мобов ({ushort.MaxValue}).");
        ArgumentOutOfRangeException.ThrowIfLessThan(health, 1);
        if (!(speed > 0) || !float.IsFinite(speed))
            throw new ArgumentOutOfRangeException(nameof(speed), speed, $"Скорость моба '{name}' должна быть положительной.");
        if (!(width > 0 && width <= 4) || !(height > 0 && height <= 4))
            throw new ArgumentOutOfRangeException(nameof(width), $"Размер моба '{name}' — от 0 до 4 блоков, получено {width} × {height}.");
        if (!ContentNames.IsValid(behavior))
            throw new ArgumentException($"Имя поведения '{behavior}' должно иметь вид 'namespace:name'.", nameof(behavior));

        var definition = new MobDefinition(NextId, name, health, speed, width, height, behavior, drops.IsDefault ? [] : drops);
        _byId.Add(definition);
        _byName.Add(name, definition);
        return definition;
    }

    public void Freeze() => IsFrozen = true;

    public bool Contains(MobId id) => id.Value >= 1 && id.Value <= _byId.Count;

    public MobDefinition Get(MobId id) =>
        Contains(id) ? _byId[id.Value - 1] : throw new KeyNotFoundException($"Моб {id} не зарегистрирован.");

    public MobDefinition Get(string name) =>
        _byName.TryGetValue(name, out var definition) ? definition : throw new KeyNotFoundException($"Моб '{name}' не зарегистрирован.");

    public bool TryGet(string name, out MobDefinition definition) => _byName.TryGetValue(name, out definition!);
}
