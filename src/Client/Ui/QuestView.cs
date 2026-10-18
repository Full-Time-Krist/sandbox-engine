using Shared.Items;
using Shared.Quests;

namespace Client.Ui;

/// <summary>Как задания показываются игроку: иконка, счёт, порядок в журнале. Без отрисовки — проверяется тестами.</summary>
internal static class QuestView
{
    /// <summary>
    /// Иконка задания — предмет цели: для «сломать» и «поставить» — предмет этого блока. Блок без предмета — воздух
    /// (иконки нет).
    /// </summary>
    public static ItemId Icon(Quest quest, ItemRegistry items) => quest.Goal.Kind is QuestGoalKind.Obtain or QuestGoalKind.Craft
        ? quest.Goal.Item
        : items.ForBlock(quest.Goal.Block);

    /// <summary>Счёт «набрано/нужно» (набранное не больше нужного); у цели из одной штуки счёта нет — пустая строка.</summary>
    public static string ProgressText(Quest quest, QuestBook book, Inventory inventory)
    {
        if (quest.Goal.Count == 1)
            return "";
        var done = book.IsCompleted(quest.Name) ? quest.Goal.Count : Math.Min(quest.Goal.Count, book.ProgressOf(quest, inventory));
        return $"{done}/{quest.Goal.Count}";
    }

    /// <summary>Доступные задания в порядке реестра: их подсказывает интерфейс.</summary>
    public static List<Quest> Available(QuestRegistry quests, QuestBook book) => [.. quests.All.Where(book.IsAvailable)];

    /// <summary>
    /// Журнал: сначала доступные, потом выполненные (в порядке реестра); недоступные не показываются — их число
    /// пишется отдельно, чтобы игрок знал, что впереди ещё есть задания.
    /// </summary>
    public static (List<Quest> Entries, int Hidden) Journal(QuestRegistry quests, QuestBook book)
    {
        var available = Available(quests, book);
        var completed = quests.All.Where(quest => book.IsCompleted(quest.Name)).ToList();
        return ([.. available, .. completed], quests.All.Length - available.Count - completed.Count);
    }

    /// <summary>
    /// Разбивает текст на строки не шире <paramref name="width"/> по словам (<paramref name="measure"/> — ширина строки).
    /// Строк больше <paramref name="maxLines"/> — последняя обрезается с многоточием. Слово шире строки режется по буквам.
    /// </summary>
    public static List<string> Wrap(string text, int width, int maxLines, Func<string, int> measure)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (measure(candidate) <= width)
            {
                line = candidate;
                continue;
            }
            if (line.Length > 0)
                lines.Add(line);
            line = word;
            while (measure(line) > width && line.Length > 1)
            {
                var fit = line.Length - 1;
                while (fit > 1 && measure(line[..fit]) > width)
                    fit--;
                lines.Add(line[..fit]);
                line = line[fit..];
            }
        }
        if (line.Length > 0)
            lines.Add(line);

        if (lines.Count <= maxLines)
            return lines;
        var last = lines[maxLines - 1];
        while (last.Length > 0 && measure(last + "…") > width)
            last = last[..^1].TrimEnd();
        lines[maxLines - 1] = last + "…";
        return lines[..maxLines];
    }
}
