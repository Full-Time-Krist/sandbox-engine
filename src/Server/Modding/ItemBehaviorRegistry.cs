using Api;
using Shared;
using Shared.Content;
using Shared.Items;

namespace Server.Modding;

/// <summary>
/// Поведения предметов по имени. Моды регистрируют их при загрузке (<see cref="IModContext.RegisterItemBehavior"/>),
/// затем <see cref="Bind"/> связывает каждый предмет с его поведением — дальше поиск по строке не нужен.
/// </summary>
internal sealed class ItemBehaviorRegistry
{
    private readonly Dictionary<string, IItemBehavior> _byName = new(StringComparer.Ordinal);

    public bool IsFrozen { get; private set; }

    public void Register(string modId, string id, IItemBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        if (IsFrozen)
            throw new InvalidOperationException($"Мод '{modId}': поведение '{id}' нельзя зарегистрировать после загрузки модов.");
        if (!ContentNames.IsValid(id))
            throw new ArgumentException($"Мод '{modId}': имя поведения '{id}' должно иметь вид 'namespace:name'.", nameof(id));
        if (id[..id.IndexOf(':')] != modId)
            throw new ArgumentException($"Мод '{modId}': поведение '{id}' не из его пространства имён ('{modId}:…').", nameof(id));
        if (!_byName.TryAdd(id, behavior))
            throw new InvalidOperationException($"Мод '{modId}': поведение '{id}' уже зарегистрировано.");
    }

    /// <summary>
    /// Замораживает реестр и возвращает поведение каждого предмета по номеру <see cref="ItemId"/>.
    /// Предмет со ссылкой на незарегистрированное поведение — ошибка контента: без поведения его нельзя применить.
    /// </summary>
    public IItemBehavior[] Bind(ItemRegistry items)
    {
        IsFrozen = true;
        var result = new IItemBehavior[items.Count];
        foreach (var item in items.All)
        {
            result[item.Id.Value] = _byName.TryGetValue(item.Behavior, out var behavior)
                ? behavior
                : throw new ContentLoadException(
                    $"Предмет '{item.Name}' ссылается на неизвестное поведение '{item.Behavior}' " +
                    "(поведения регистрирует код модов через IModContext.RegisterItemBehavior).");
        }
        return result;
    }
}
