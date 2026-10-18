using Shared.Items;

namespace Shared.Quests;

/// <summary>
/// Задания одного игрока: какие выполнены и сколько набрано у считаемых (<see cref="QuestGoal.IsCounted"/>).
/// Авторитетная копия — на сервере (она же в сохранении), клиент держит свою для журнала и подсказок.
/// </summary>
/// <remarks>
/// Хранит имена, а не ссылки на определения: выполненное задание из мода, которого сейчас нет, остаётся в книге
/// и вернётся вместе с модом.
/// </remarks>
public sealed class QuestBook
{
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _progress = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Completed => _completed;

    /// <summary>Набранное у невыполненных считаемых заданий (у выполненных не хранится).</summary>
    public IReadOnlyDictionary<string, int> Progress => _progress;

    public bool IsCompleted(string quest) => _completed.Contains(quest);

    /// <summary>Доступно: ещё не выполнено, а все предшествующие выполнены.</summary>
    public bool IsAvailable(Quest quest) => !_completed.Contains(quest.Name) && quest.Requires.All(_completed.Contains);

    /// <summary>Набранное у считаемого задания; у задания «иметь предмет» — сколько его сейчас в инвентаре.</summary>
    public int ProgressOf(Quest quest, Inventory inventory) => quest.Goal.IsCounted
        ? _progress.GetValueOrDefault(quest.Name)
        : inventory.Count(quest.Goal.Item);

    /// <summary>Отмечает задание выполненным. Возвращает false, если оно уже было выполнено.</summary>
    public bool Complete(string quest)
    {
        _progress.Remove(quest);
        return _completed.Add(quest);
    }

    /// <summary>Записывает набранное. Отрицательное — ошибка: оно может прийти только из испорченных данных.</summary>
    public void SetProgress(string quest, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            _progress.Remove(quest);
        else
            _progress[quest] = count;
    }
}
