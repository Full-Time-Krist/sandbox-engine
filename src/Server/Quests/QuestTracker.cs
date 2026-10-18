using Api;
using Server.Modding;
using Shared;
using Shared.Protocol;
using Shared.Quests;

namespace Server.Quests;

/// <summary>
/// Ведёт задания игроков: считает действия (сделать, сломать, поставить), проверяет «иметь предмет» по инвентарю,
/// отмечает выполненные, сообщает клиенту (<see cref="QuestChanged"/>) и модам (<see cref="QuestCompleted"/>).
/// </summary>
/// <remarks>
/// Задания идут только в выживании: в творческом предметы берутся из палитры, и «достижения» ничего бы не значили.
/// Действие засчитывается заданиям, доступным в момент действия: задание, ставшее доступным от этого же действия,
/// начинает счёт со следующего.
/// </remarks>
internal sealed class QuestTracker(QuestRegistry quests, GameEvents events)
{
    public QuestRegistry Quests => quests;

    /// <summary>Действие игрока: <paramref name="amount"/> раз по цели вида <paramref name="kind"/>, подходящей под <paramref name="matches"/>.</summary>
    public void Count(ClientSession session, QuestGoalKind kind, Func<QuestGoal, bool> matches, int amount, IWorld world)
    {
        if (session.Mode != GameMode.Survival)
            return;

        var book = session.Quests;
        // Сначала — какие задания засчитывают действие, потом — счёт: выполненное здесь не должно открыть следующее
        // и тут же засчитать ему то же действие.
        var counted = quests.All.Where(quest => quest.Goal.Kind == kind && book.IsAvailable(quest) && matches(quest.Goal)).ToArray();
        var completed = false;
        foreach (var quest in counted)
        {
            var progress = (int)Math.Min(quest.Goal.Count, (long)book.Progress.GetValueOrDefault(quest.Name) + amount);
            if (progress >= quest.Goal.Count)
            {
                Complete(session, quest, world);
                completed = true;
            }
            else
            {
                book.SetProgress(quest.Name, progress);
                session.Connection.Send(new QuestChanged(quest.Name, progress, Completed: false));
            }
        }

        // Выполненное могло открыть «иметь предмет», который уже в инвентаре.
        if (completed)
            CheckObtain(session, world);
    }

    /// <summary>
    /// Задания «иметь предмет»: выполнены, если в инвентаре уже столько штук. Одного прохода хватает и на цепочки:
    /// реестр упорядочен по глубине, требуемое задание всегда раньше требующего, и выполненное в этом проходе
    /// открывает следующие до того, как до них дойдёт очередь.
    /// </summary>
    public void CheckObtain(ClientSession session, IWorld world)
    {
        if (session.Mode != GameMode.Survival)
            return;

        foreach (var quest in quests.All)
        {
            if (quest.Goal.Kind == QuestGoalKind.Obtain && session.Quests.IsAvailable(quest)
                && session.Inventory.Count(quest.Goal.Item) >= quest.Goal.Count)
                Complete(session, quest, world);
        }
    }

    private void Complete(ClientSession session, Quest quest, IWorld world)
    {
        session.Quests.Complete(quest.Name);
        session.Connection.Send(new QuestChanged(quest.Name, quest.Goal.Count, Completed: true));
        Console.WriteLine($"[Server] {session.PlayerName} выполнил задание '{quest.Name}'");
        events.Raise(new QuestCompleted(world, session, quest));
    }
}
