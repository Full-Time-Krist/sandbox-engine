using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using Server.Items;
using Server.Storage;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Physics;
using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Владелец мира: вся игровая логика выполняется здесь, клиенты только присылают действия и получают результат.
/// </summary>
/// <remarks>
/// Сервер не знает, кто и в каком потоке вызывает <see cref="Tick"/>: в одиночной игре это <see cref="ServerHost"/>,
/// в тестах — сам тест. Всё состояние сервера меняется только внутри <see cref="Tick"/>, из одного потока.
/// </remarks>
public sealed class GameServer : IDisposable
{
    /// <summary>
    /// Насколько присланная точка взгляда может отличаться от глаза игрока в последней известной позиции.
    /// Клиент рисует камеру с интерполяцией и шлёт позицию раз в тик, так что небольшое расхождение нормально;
    /// большое означает ошибку или попытку действовать издалека.
    /// </summary>
    private const float MaxEyeOffset = 4f;

    private readonly List<ClientSession> _sessions = [];

    /// <summary>Подключения, которые ещё не приняты в тике. Пополняются из любого потока.</summary>
    private readonly ConcurrentQueue<IConnection<ServerMessage, ClientMessage>> _pendingConnections = new();
    private readonly ImmutableArray<string> _blockNames;
    private readonly ImmutableArray<string> _itemNames;
    private readonly BlockRegistry _registry;
    private readonly BaseBlocks _blocks;
    private readonly ItemRegistry _items;
    private readonly WorldEditor _editor;
    /// <summary>
    /// Сколько чанков отправлять одному игроку за тик. Иначе при входе клиент получил бы сотни чанков разом:
    /// и сервер сжимал бы их в одном тике, и клиент строил бы их меши в одном кадре.
    /// </summary>
    public const int MaxChunksPerTick = 16;

    /// <summary>Как часто сервер выгружает чанки, рядом с которыми нет игроков: раз в секунду.</summary>
    private const int UnloadIntervalTicks = GameTime.TicksPerSecond;

    /// <summary>Автосохранение раз в минуту: при сбое теряется не больше минуты игры.</summary>
    public const int AutosaveIntervalTicks = 60 * GameTime.TicksPerSecond;

    /// <summary>Дальность прорисовки по умолчанию, в чанках: 6 — 192 блока по горизонтали, 4 — 128 блоков вверх и вниз.</summary>
    public const int DefaultViewDistance = 6;
    public const int DefaultVerticalDistance = 4;

    /// <summary>Мир по int тянется до ±2 миллиардов блоков; берём с запасом, чтобы радиусы вокруг игрока не переполнились.</summary>
    private const double MaxCoordinate = 1_000_000_000;

    private readonly WorldSave? _save;
    private readonly int _viewDistance;
    private readonly int _verticalDistance;
    private int _nextPlayerId = 1;
    private long _tick;
    private int _unloadedSinceLog;

    public World World { get; }

    /// <summary>Сохранение мира или null, если мир только в памяти. Для тестов: дождаться записи на диск.</summary>
    internal WorldSave? Save => _save;

    /// <param name="seed">Сид нового мира: одинаковый сид — одинаковый мир; null — случайный.
    /// У мира из сохранения сид свой, аргумент не действует.</param>
    /// <param name="generatorThreads">Сколько фоновых потоков генерирует чанки; null — по числу ядер.</param>
    /// <param name="viewDistance">Дальность прорисовки по горизонтали, в чанках. Тесты берут маленькую, чтобы не ждать генерации.</param>
    /// <param name="verticalDistance">Дальность прорисовки вверх и вниз, в чанках.</param>
    /// <param name="spawnColumn">Столбец точки появления нового мира; null — (0, 0).</param>
    /// <param name="savePath">Файл сохранения: есть — мир загружается, нет — создаётся. null — мир только в памяти (тесты).</param>
    /// <param name="defaultMode">Режим нового игрока в новом мире; null — выживание. У мира из сохранения режим свой.</param>
    public GameServer(int? seed, int? generatorThreads = null,
        int viewDistance = DefaultViewDistance, int verticalDistance = DefaultVerticalDistance,
        (int X, int Z)? spawnColumn = null, string? savePath = null, GameMode? defaultMode = null)
    {
        _viewDistance = viewDistance;
        _verticalDistance = verticalDistance;

        // Блоки и предметы — из JSON-контента (GameContent); клиент грузит те же файлы и сверяет имена при подключении.
        var content = GameContent.Load();
        var registry = _registry = content.Blocks;
        _blocks = BaseBlocks.From(registry);
        _blockNames = [.. registry.All.Select(b => b.Name)];

        _items = content.Items;
        _itemNames = [.. _items.All.Select(i => i.Name)];

        if (savePath != null)
            _save = WorldSave.Open(savePath, registry, _items);
        try
        {
            var isNew = _save?.Info == null;
            World = new World(_blocks, seed ?? Random.Shared.Next(), generatorThreads ?? ChunkGenerator.DefaultWorkerCount,
                spawnColumn ?? default, _save, defaultMode ?? GameMode.Survival);
            if (_save != null)
                LogOpened(isNew, seed, spawnColumn, defaultMode);
        }
        catch
        {
            _save?.Dispose();
            throw;
        }

        _editor = new WorldEditor(this);
    }

    private void LogOpened(bool isNew, int? seed, (int X, int Z)? spawnColumn, GameMode? defaultMode)
    {
        var mode = World.DefaultMode.ToName();
        Console.WriteLine(isNew
            ? $"[Server] Создан новый мир (сид {World.Seed}, режим {mode}): {_save!.Path}"
            : $"[Server] Загружен мир (сид {World.Seed}, режим {mode}): {_save!.Path}");
        if (isNew)
            return;

        if (_save.UpgradedFrom is { } from)
            Console.WriteLine($"[Server] Сохранение обновлено с формата {from} до {WorldSave.FormatVersion}");
        if (seed != null && seed != World.Seed)
            Console.WriteLine($"[Server] Сид {seed} не применён: у мира уже есть свой");
        if (spawnColumn != null)
            Console.WriteLine("[Server] Точка появления не применена: у мира уже есть своя");
        if (defaultMode != null && defaultMode != World.DefaultMode)
            Console.WriteLine($"[Server] Режим {defaultMode.Value.ToName()} не применён: у мира уже есть свой");
        foreach (var name in _save.BlockIds.UnknownNames)
            Console.WriteLine($"[Server] Блок '{name}' из сохранения неизвестен, вместо него будет воздух");
        foreach (var name in _save.ItemIds.UnknownNames)
            Console.WriteLine($"[Server] Предмет '{name}' из сохранения неизвестен, он пропадёт из инвентарей");
    }

    /// <summary>
    /// Подключает клиента. Можно вызывать из любого потока: сессия появится в начале следующего тика,
    /// потому что список сессий принадлежит потоку сервера.
    /// </summary>
    public void Connect(IConnection<ServerMessage, ClientMessage> connection) => _pendingConnections.Enqueue(connection);

    public void Tick()
    {
        while (_pendingConnections.TryDequeue(out var connection))
            _sessions.Add(new ClientSession(connection));

        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                session.Movement.Refill();
            while (session.Connection.TryReceive(out var message))
                Handle(session, message);
        }

        World.CollectGenerated();
        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                StreamChunks(session);
        }

        if (++_tick % UnloadIntervalTicks == 0)
            UnloadUnusedChunks();
        if (_tick % AutosaveIntervalTicks == 0)
            Autosave();

        // Изменения инвентаря за тик — одним сообщением, в конце: что бы ни меняло инвентарь в этом тике, оно уже учтено.
        foreach (var session in _sessions)
        {
            if (session.IsJoined && session.Inventory.TakeChanges() is { IsEmpty: false } changes)
                session.Connection.Send(new InventoryChanged(changes));
        }
    }

    /// <summary>Ставит в очередь записи изменённые чанки и позиции игроков. Возвращает число чанков.</summary>
    private int SaveAll()
    {
        if (_save == null)
            return 0;

        foreach (var session in _sessions)
        {
            if (session.IsJoined)
            {
                _save.SavePlayer(session.PlayerName, new SavedPlayer(session.PlayerPosition, session.Look, session.Mode,
                    session.Inventory.SelectedSlot, session.Inventory.CopySlots()));
            }
        }
        return World.SaveDirty();
    }

    private void Autosave()
    {
        var chunks = SaveAll();
        if (chunks > 0)
            Console.WriteLine($"[Server] Автосохранение: изменённых чанков {chunks}");
    }

    /// <summary>
    /// Подгрузка вокруг игрока: выгружает у клиента ушедшие далеко чанки, отправляет готовые недостающие
    /// (не больше <see cref="MaxChunksPerTick"/>) и ставит в генерацию те, которых ещё нет. Всё — от ближних к дальним.
    /// </summary>
    private void StreamChunks(ClientSession session)
    {
        var view = session.View;
        foreach (var position in view.MoveTo(BlockPos.Containing(session.PlayerPosition).Chunk))
            session.Connection.Send(new ChunkUnload(position));

        var budget = MaxChunksPerTick;
        var canRequest = true;
        foreach (var position in view.Missing())
        {
            if (World.Chunks.Get(position) is { } chunk)
            {
                if (budget == 0)
                    continue;
                session.Connection.Send(ChunkData.From(chunk));
                view.MarkSent(position);
                budget--;
            }
            else if (canRequest)
            {
                canRequest = World.TryRequest(position);
            }

            if (budget == 0 && !canRequest)
                break;
        }
    }

    /// <summary>Выгружает из памяти сервера чанки, которые не нужны ни одному игроку.</summary>
    private void UnloadUnusedChunks()
    {
        _unloadedSinceLog += World.Unload(position => _sessions.Any(s => s.IsJoined && s.View.Keeps(position)));

        // Сводка раз в 10 секунд, а не каждую выгрузку: в полёте выгрузка идёт каждую секунду.
        if (_tick % (UnloadIntervalTicks * 10) == 0 && _unloadedSinceLog > 0)
        {
            Console.WriteLine($"[Server] За 10 с выгружено {_unloadedSinceLog} чанков, в памяти {World.Chunks.Count}; " +
                $"с запуска загружено из сохранения {World.LoadedCount}, сгенерировано {World.GeneratedCount}");
            _unloadedSinceLog = 0;
        }
    }

    /// <summary>
    /// Останавливает фоновые потоки генерации и сохраняет мир. Вызывать после остановки потока сервера:
    /// состояние сервера принадлежит ему.
    /// </summary>
    public void Dispose()
    {
        World.Dispose();
        if (_save == null)
            return;

        // Генерация остановлена: в очередь записи больше никто не пишет, и после Dispose всё будет на диске.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int chunks;
        try
        {
            chunks = SaveAll();
        }
        finally
        {
            // Даже если поставить в очередь не вышло (упал поток записи), файл нужно закрыть.
            _save.Dispose();
        }
        Console.WriteLine($"[Server] Мир сохранён (изменённых чанков {chunks}) за {clock.ElapsedMilliseconds} мс");
    }

    private void Handle(ClientSession session, ClientMessage message)
    {
        switch (message)
        {
            case JoinRequest join when !session.IsJoined:
                HandleJoin(session, join);
                break;

            case PlayerMove move when session.IsJoined:
                HandleMove(session, move);
                break;

            case SelectHotbarSlot select when session.IsJoined:
                if (!session.Inventory.TrySelect(select.Slot))
                    Console.WriteLine($"[Server] {session.PlayerName}: неверный слот {select.Slot}");
                break;

            case SetGameMode setMode when session.IsJoined:
                // Прав доступа пока нет, а игра одиночная: сменить режим может любой игрок.
                if (setMode.Mode.IsDefined())
                    ChangeMode(session, setMode.Mode);
                else
                    Console.WriteLine($"[Server] {session.PlayerName}: неизвестный режим {(int)setMode.Mode}");
                break;

            case InventoryClick click when session.IsJoined:
                if (!session.Inventory.Click(click.Slot, click.Kind))
                    Console.WriteLine($"[Server] {session.PlayerName}: неверный клик по инвентарю ({click.Slot}, {click.Kind})");
                break;

            case ReturnInventoryCursor when session.IsJoined:
                session.Inventory.ReturnCursor();
                break;

            case InventoryDragDistribute drag when session.IsJoined:
                if (!session.Inventory.DistributeCursor(drag.TargetSlots, drag.OnePerSlot))
                    Console.WriteLine($"[Server] {session.PlayerName}: неверные цели перетаскивания инвентаря");
                break;

            case CreativeGiveItem give when session.IsJoined && session.Mode == GameMode.Creative:
                if (_items.Contains(give.Item) && !give.Item.IsAir)
                {
                    var definition = _items.Get(give.Item);
                    session.Inventory.Add(new ItemStack(give.Item, definition.MaxStackSize));
                }
                break;

            case UseItem use when session.IsJoined:
                HandleUseItem(session, use);
                break;

            default:
                // До JoinRequest принимается только он сам. Незнакомое сообщение — ошибка протокола, а не повод падать.
                Console.WriteLine($"[Server] Сообщение {message.GetType().Name} отклонено (joined: {session.IsJoined})");
                break;
        }
    }

    /// <summary>
    /// Игрок, который уже был в этом мире, продолжает с того места, где вышел: с тем же взглядом, режимом и инвентарём.
    /// Новый появляется в точке появления, в режиме мира и с пустым инвентарём.
    /// </summary>
    private void HandleJoin(ClientSession session, JoinRequest join)
    {
        var saved = _save?.LoadPlayer(join.PlayerName);
        var start = saved != null && IsValidPosition(saved.Position) ? saved.Position : World.SpawnPosition;
        var mode = saved?.Mode ?? World.DefaultMode;
        var inventory = new Inventory(_items);
        if (saved != null)
            RestoreInventory(inventory, saved);

        session.Join(_nextPlayerId++, join.PlayerName, mode, inventory,
            new PlayerChunkView(_viewDistance, _verticalDistance, BlockPos.Containing(start).Chunk));
        // До первого PlayerMove игрок стоит в начальной точке, иначе его действия сочлись бы «издалека».
        session.PlayerPosition = start;
        session.Look = saved?.Look ?? default;
        // Чанки придут в следующих тиках (StreamChunks), начиная с ближайших.
        session.Connection.Send(new JoinAccepted(session.PlayerId, start, session.Look, mode, _blockNames, _itemNames));
        // Инвентарь уходит целиком, поэтому изменения от его заполнения отправлять не нужно.
        session.Connection.Send(new InventoryContents(inventory.CopySlots(), inventory.SelectedSlot));
        inventory.TakeChanges();
        Console.WriteLine($"[Server] {session.PlayerName} подключился (id {session.PlayerId}, режим {mode.ToName()})");
    }

    /// <summary>
    /// Кладёт сохранённые стопки в инвентарь. Предмет, который при выходе был на курсоре, возвращается в слоты:
    /// экран инвентаря при входе закрыт, и на курсоре его не было бы видно.
    /// </summary>
    private static void RestoreInventory(Inventory inventory, SavedPlayer saved)
    {
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
            inventory.Set(slot, saved.Slots[slot]);
        inventory.ReturnCursor();
        inventory.TrySelect(saved.SelectedSlot);
    }

    /// <summary>
    /// Меняет режим игрока и сообщает клиенту. При переходе в выживание пределы движения становятся строже,
    /// а клиент ещё тик-другой летит по-творчески: его уже отправленные перемещения сервер отклонил бы.
    /// Телепорт на месте делает их устаревшими (старый номер) и гасит скорость у клиента.
    /// </summary>
    private static void ChangeMode(ClientSession session, GameMode mode)
    {
        if (session.Mode == mode)
            return;

        session.SetMode(mode);
        session.Connection.Send(new GameModeChanged(mode));
        if (mode == GameMode.Survival)
            SendTeleport(session);
        Console.WriteLine($"[Server] {session.PlayerName}: режим {mode.ToName()}");
    }

    /// <summary>Меняет режим игрока, как по его запросу. Для тестов (позже — для команд).</summary>
    internal void SetGameMode(int playerId, GameMode mode) => ChangeMode(_sessions.Single(s => s.IsJoined && s.PlayerId == playerId), mode);

    private void HandleMove(ClientSession session, PlayerMove move)
    {
        // Отправлено до последнего телепорта и посчитано от старой позиции.
        if (move.TeleportId != session.Movement.TeleportId)
            return;

        // Взгляд не проверяется (он нужен только для сохранения), но NaN и огромные углы в сохранение не пускаем.
        if (move.Look.IsFinite)
            session.Look = move.Look.Normalized();

        // От позиции зависит, какие чанки грузить: NaN или координаты за пределами int сломали бы расчёт.
        var problem = IsValidPosition(move.Position)
            ? session.Movement.Check(session.PlayerPosition, move.Position, World.Chunks, _registry)
            : "неверная позиция";
        if (problem == null)
        {
            session.PlayerPosition = move.Position;
            return;
        }

        Console.WriteLine($"[Server] {session.PlayerName}: движение в {move.Position} отклонено ({problem}), возврат в {session.PlayerPosition}");
        SendTeleport(session);
    }

    /// <summary>
    /// Переносит игрока (ноги в <paramref name="feet"/>) и сообщает клиенту. Только из потока сервера:
    /// для будущих команд и для тестов, которым нужно переместить игрока далеко.
    /// </summary>
    internal void Teleport(int playerId, Vector3d feet)
    {
        if (!IsValidPosition(feet))
            throw new ArgumentOutOfRangeException(nameof(feet), feet, "Неверная позиция");

        var session = _sessions.Single(s => s.IsJoined && s.PlayerId == playerId);
        session.PlayerPosition = feet;
        SendTeleport(session);
    }

    /// <summary>Последняя принятая позиция игрока (точка ног). Для тестов.</summary>
    internal Vector3d PlayerPosition(int playerId) => _sessions.Single(s => s.IsJoined && s.PlayerId == playerId).PlayerPosition;

    /// <summary>Инвентарь игрока на сервере. Для тестов: менять его можно только между тиками, из потока теста.</summary>
    internal Inventory PlayerInventory(int playerId) => _sessions.Single(s => s.IsJoined && s.PlayerId == playerId).Inventory;

    private static void SendTeleport(ClientSession session) =>
        session.Connection.Send(new PlayerTeleport(session.PlayerPosition, session.Movement.NextTeleport()));

    private void HandleUseItem(ClientSession session, UseItem use)
    {
        if (!IsValidPosition(use.EyePosition) || !float.IsFinite(use.LookDirection.LengthSquared()))
            return;

        // Позиция игрока — точка ног, глаз выше на EyeHeight.
        var eye = session.PlayerPosition + new Vector3d(0, PlayerPhysics.EyeHeight, 0);
        if (Vector3d.Distance(use.EyePosition, eye) > MaxEyeOffset)
        {
            Console.WriteLine($"[Server] {session.PlayerName}: действие отклонено, точка взгляда далеко от игрока");
            return;
        }

        // Сервер сам повторяет луч по своей копии мира: клиент не может указать произвольный блок.
        var target = VoxelRaycast.Cast(World.Chunks, use.EyePosition, use.LookDirection, GameRules.BlockReach);
        var held = _items.Get(session.Inventory.Selected.Item);
        ItemBehaviors.For(held).Use(use.Action,
            new ItemUseContext(_editor, _items, session.Inventory, session.Mode, target, use.EyePosition));
    }

    private static bool IsValidPosition(Vector3d position) =>
        position.IsFinite
        && Math.Abs(position.X) < MaxCoordinate && Math.Abs(position.Y) < MaxCoordinate && Math.Abs(position.Z) < MaxCoordinate;

    /// <summary>Сообщает об изменении блока игрокам, у которых есть этот чанк: остальным он не нужен.</summary>
    private void BroadcastBlockChange(BlockChanged change)
    {
        var chunk = change.Position.Chunk;
        foreach (var session in _sessions)
        {
            if (session.IsJoined && session.View.WasSent(chunk))
                session.Connection.Send(change);
        }
    }

    /// <summary>Единственная точка изменения блоков на сервере: меняет мир и сообщает игрокам.</summary>
    private sealed class WorldEditor(GameServer server) : IBlockEditor
    {
        public BlockId GetBlock(BlockPos pos) => server.World.Chunks.GetBlock(pos);

        public bool IsBlockedByPlayer(BlockPos pos, BlockId block)
        {
            if (!server._registry.IsSolid(block))
                return false;

            var box = Aabb.OfBlock(pos);
            foreach (var session in server._sessions)
            {
                if (session.IsJoined && PlayerPhysics.BodyAt(session.PlayerPosition).Intersects(box))
                    return true;
            }
            return false;
        }

        public bool TrySetBlock(BlockPos pos, BlockId block)
        {
            if (!server.World.TrySetBlock(pos, block))
                return false;

            server.BroadcastBlockChange(new BlockChanged(pos, block));
            return true;
        }
    }
}
