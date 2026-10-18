using System.Collections.Immutable;
using Shared.Blocks;
using Shared.Items;

namespace Shared.Quests;

/// <summary>Что нужно сделать в задании.</summary>
public enum QuestGoalKind
{
    /// <summary>Иметь в инвентаре сразу столько штук предмета (курсор не считается).</summary>
    Obtain,

    /// <summary>Сделать по рецептам столько штук предмета (считается с момента, когда задание стало доступным).</summary>
    Craft,

    /// <summary>Сломать столько блоков (считается с момента, когда задание стало доступным).</summary>
    Break,

    /// <summary>Поставить столько блоков (считается с момента, когда задание стало доступным).</summary>
    Place,
}

/// <param name="Item">Предмет для <see cref="QuestGoalKind.Obtain"/> и <see cref="QuestGoalKind.Craft"/>, иначе воздух.</param>
/// <param name="Block">Блок для <see cref="QuestGoalKind.Break"/> и <see cref="QuestGoalKind.Place"/>, иначе воздух.</param>
/// <param name="Count">Сколько нужно, не меньше одного.</param>
public sealed record QuestGoal(QuestGoalKind Kind, ItemId Item, BlockId Block, int Count)
{
    /// <summary>Цель считается по событиям (сделать, сломать, поставить), а не по инвентарю.</summary>
    public bool IsCounted => Kind != QuestGoalKind.Obtain;
}

/// <summary>Задание: название и описание для игрока, цель и задания, которые нужно выполнить раньше.</summary>
/// <param name="Name">Постоянное имя <c>namespace:name</c>: под ним выполненное задание хранится в сохранении игрока.</param>
/// <param name="Requires">Имена заданий, после которых это становится доступным; пусто — доступно сразу.</param>
public sealed record Quest(string Name, string Title, string Description, QuestGoal Goal, ImmutableArray<string> Requires);

/// <summary>
/// Все задания в порядке показа: сначала те, что доступны сразу, затем по глубине цепочки (на сколько шагов задание
/// дальше от начала), внутри одной глубины — по именам. Так журнал идёт по цепочке, а порядок не зависит от файлов.
/// </summary>
public sealed class QuestRegistry
{
    private readonly Dictionary<string, Quest> _byName;

    /// <param name="quests">Задания с разными именами; ссылки <see cref="Quest.Requires"/> — на задания из этого же набора, без циклов.</param>
    public QuestRegistry(IEnumerable<Quest> quests)
    {
        ArgumentNullException.ThrowIfNull(quests);
        _byName = quests.ToDictionary(quest => quest.Name, StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        All = [.. _byName.Values.OrderBy(quest => Depth(quest.Name, depth)).ThenBy(quest => quest.Name, StringComparer.Ordinal)];
    }

    public ImmutableArray<Quest> All { get; }

    public bool TryGet(string name, out Quest quest) => _byName.TryGetValue(name, out quest!);

    public Quest Get(string name) =>
        _byName.TryGetValue(name, out var quest) ? quest : throw new KeyNotFoundException($"Задание '{name}' не найдено.");

    private int Depth(string name, Dictionary<string, int> known)
    {
        if (known.TryGetValue(name, out var depth))
            return depth;
        var requires = _byName[name].Requires;
        depth = requires.IsEmpty ? 0 : 1 + requires.Max(required => Depth(required, known));
        known[name] = depth;
        return depth;
    }
}
