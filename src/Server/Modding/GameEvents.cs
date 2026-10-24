using Api;

namespace Server.Modding;

/// <summary>
/// События мира для модов. Действие игрока только ставит событие в очередь (<see cref="Raise(BlockBroken)"/>), а обработчики
/// вызываются в <see cref="Dispatch"/> — после того, как сервер закончил обрабатывать сообщение. Так обработчик видит
/// действие завершённым (добыча уже в инвентаре) и не вклинивается в середину чужого кода.
/// </summary>
/// <remarks>
/// Обработчик может сам вызвать действие с событием (сломать ещё блок) — оно встанет в ту же очередь и разберётся в этом же
/// <see cref="Dispatch"/>. Чтобы два мода, отвечающие друг другу, не повесили сервер, за один разбор обрабатывается не больше
/// <see cref="MaxEventsPerDispatch"/> событий; остальные отбрасываются с записью в лог.
/// </remarks>
internal sealed class GameEvents(Action<string> log)
{
    public const int MaxEventsPerDispatch = 10_000;

    private readonly List<(string ModId, Action<BlockBroken> Handler)> _blockBroken = [];
    private readonly List<(string ModId, Action<BlockPlaced> Handler)> _blockPlaced = [];
    private readonly List<(string ModId, Action<PlayerJoined> Handler)> _playerJoined = [];
    private readonly List<(string ModId, Action<ItemCrafted> Handler)> _itemCrafted = [];
    private readonly List<(string ModId, Action<QuestCompleted> Handler)> _questCompleted = [];
    private readonly List<(string ModId, Action<ItemPickedUp> Handler)> _itemPickedUp = [];
    private readonly List<(string ModId, Action<PlayerDamaged> Handler)> _playerDamaged = [];
    private readonly List<(string ModId, Action<PlayerDied> Handler)> _playerDied = [];
    private readonly Queue<Action> _pending = new();

    /// <summary>Подписки закрыты: моды загружены.</summary>
    public bool IsFrozen { get; private set; }

    public void Freeze() => IsFrozen = true;

    /// <summary>Подписка от имени мода: его id попадёт в лог, если обработчик упадёт.</summary>
    public IModEvents For(string modId) => new ModEvents(this, modId);

    public void Raise(BlockBroken e) => Enqueue(_blockBroken, e, nameof(BlockBroken));
    public void Raise(BlockPlaced e) => Enqueue(_blockPlaced, e, nameof(BlockPlaced));
    public void Raise(PlayerJoined e) => Enqueue(_playerJoined, e, nameof(PlayerJoined));
    public void Raise(ItemCrafted e) => Enqueue(_itemCrafted, e, nameof(ItemCrafted));
    public void Raise(QuestCompleted e) => Enqueue(_questCompleted, e, nameof(QuestCompleted));
    public void Raise(ItemPickedUp e) => Enqueue(_itemPickedUp, e, nameof(ItemPickedUp));
    public void Raise(PlayerDamaged e) => Enqueue(_playerDamaged, e, nameof(PlayerDamaged));
    public void Raise(PlayerDied e) => Enqueue(_playerDied, e, nameof(PlayerDied));

    /// <summary>Вызывает обработчики всех событий в очереди, включая появившиеся по ходу. Только из потока сервера.</summary>
    public void Dispatch()
    {
        var dispatched = 0;
        while (_pending.TryDequeue(out var next))
        {
            if (++dispatched > MaxEventsPerDispatch)
            {
                log($"[Mods] За одно действие больше {MaxEventsPerDispatch} событий — вероятно, обработчики модов " +
                    $"вызывают друг друга бесконечно. Отброшено событий: {_pending.Count + 1}");
                _pending.Clear();
                return;
            }
            next();
        }
    }

    private void Enqueue<T>(List<(string ModId, Action<T> Handler)> handlers, T e, string name)
    {
        // Без подписчиков событие никому не нужно: не тратим на него очередь.
        if (handlers.Count > 0)
            _pending.Enqueue(() => Invoke(handlers, e, name));
    }

    private void Invoke<T>(List<(string ModId, Action<T> Handler)> handlers, T e, string name)
    {
        foreach (var (modId, handler) in handlers)
        {
            try
            {
                handler(e);
            }
            catch (Exception exception)
            {
                log($"[Mod {modId}] Ошибка в обработчике {name}: {exception}");
            }
        }
    }

    private void Subscribe<T>(List<(string ModId, Action<T> Handler)> handlers, string modId, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (IsFrozen)
            throw new InvalidOperationException($"Мод '{modId}': подписываться на события можно только при загрузке (IMod.Load).");
        handlers.Add((modId, handler));
    }

    private sealed class ModEvents(GameEvents events, string modId) : IModEvents
    {
        public void OnBlockBroken(Action<BlockBroken> handler) => events.Subscribe(events._blockBroken, modId, handler);
        public void OnBlockPlaced(Action<BlockPlaced> handler) => events.Subscribe(events._blockPlaced, modId, handler);
        public void OnPlayerJoined(Action<PlayerJoined> handler) => events.Subscribe(events._playerJoined, modId, handler);
        public void OnItemCrafted(Action<ItemCrafted> handler) => events.Subscribe(events._itemCrafted, modId, handler);
        public void OnQuestCompleted(Action<QuestCompleted> handler) => events.Subscribe(events._questCompleted, modId, handler);
        public void OnItemPickedUp(Action<ItemPickedUp> handler) => events.Subscribe(events._itemPickedUp, modId, handler);
        public void OnPlayerDamaged(Action<PlayerDamaged> handler) => events.Subscribe(events._playerDamaged, modId, handler);
        public void OnPlayerDied(Action<PlayerDied> handler) => events.Subscribe(events._playerDied, modId, handler);
    }
}
