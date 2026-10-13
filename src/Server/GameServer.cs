using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using Server.Items;
using Server.Storage;
using Shared;
using Shared.Blocks;
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
    private readonly BlockRegistry _registry;
    private readonly BaseBlocks _blocks;
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
    public GameServer(int? seed, int? generatorThreads = null,
        int viewDistance = DefaultViewDistance, int verticalDistance = DefaultVerticalDistance,
        (int X, int Z)? spawnColumn = null, string? savePath = null)
    {
        _viewDistance = viewDistance;
        _verticalDistance = verticalDistance;

        var registry = _registry = new BlockRegistry();
        _blocks = BaseBlocks.Register(registry);
        registry.Freeze();
        _blockNames = [.. registry.All.Select(b => b.Name)];

        if (savePath != null)
            _save = WorldSave.Open(savePath, registry);
        try
        {
            var isNew = _save?.Info == null;
            World = new World(_blocks, seed ?? Random.Shared.Next(), generatorThreads ?? ChunkGenerator.DefaultWorkerCount,
                spawnColumn ?? default, _save);
            if (_save != null)
                LogOpened(isNew, seed, spawnColumn);
        }
        catch
        {
            _save?.Dispose();
            throw;
        }

        _editor = new WorldEditor(this);
    }

    private void LogOpened(bool isNew, int? seed, (int X, int Z)? spawnColumn)
    {
        Console.WriteLine(isNew
            ? $"[Server] Создан новый мир (сид {World.Seed}): {_save!.Path}"
            : $"[Server] Загружен мир (сид {World.Seed}): {_save!.Path}");
        if (isNew)
            return;

        if (seed != null && seed != World.Seed)
            Console.WriteLine($"[Server] Сид {seed} не применён: у мира уже есть свой");
        if (spawnColumn != null)
            Console.WriteLine("[Server] Точка появления не применена: у мира уже есть своя");
        foreach (var name in _save!.BlockIds.UnknownNames)
            Console.WriteLine($"[Server] Блок '{name}' из сохранения неизвестен, вместо него будет воздух");
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
    }

    /// <summary>Ставит в очередь записи изменённые чанки и позиции игроков. Возвращает число чанков.</summary>
    private int SaveAll()
    {
        if (_save == null)
            return 0;

        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                _save.SavePlayer(session.PlayerName, session.PlayerPosition);
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
                // Игрок, который уже был в этом мире, продолжает с того места, где вышел.
                var start = _save?.LoadPlayer(join.PlayerName) is { } saved && IsValidPosition(saved) ? saved : World.SpawnPosition;
                session.Join(_nextPlayerId++, join.PlayerName, Hotbar.CreateDefault(_blocks),
                    new PlayerChunkView(_viewDistance, _verticalDistance, BlockPos.Containing(start).Chunk));
                // До первого PlayerMove игрок стоит в начальной точке, иначе его действия сочлись бы «издалека».
                session.PlayerPosition = start;
                // Чанки придут в следующих тиках (StreamChunks), начиная с ближайших.
                session.Connection.Send(new JoinAccepted(session.PlayerId, start, _blockNames));
                Console.WriteLine($"[Server] {session.PlayerName} подключился (id {session.PlayerId})");
                break;

            case PlayerMove move when session.IsJoined:
                // От позиции зависит, какие чанки грузить: NaN или координаты за пределами int сломали бы расчёт.
                if (IsValidPosition(move.Position))
                    session.PlayerPosition = move.Position;
                else
                    Console.WriteLine($"[Server] {session.PlayerName}: неверная позиция {move.Position}");
                break;

            case SelectHotbarSlot select when session.IsJoined:
                if (!session.Hotbar.TrySelect(select.Slot))
                    Console.WriteLine($"[Server] {session.PlayerName}: неверный слот {select.Slot}");
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
        var item = session.Hotbar.SelectedItem;
        ItemBehaviors.For(item).Use(use.Action, new ItemUseContext(_editor, item, target, use.EyePosition));
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
