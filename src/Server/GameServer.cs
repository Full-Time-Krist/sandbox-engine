using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using Api;
using Server.Entities;
using Server.Modding;
using Server.Quests;
using Server.Storage;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Entities;
using Shared.Items;
using Shared.Lighting;
using Shared.Modding;
using Shared.Physics;
using Shared.Networking;
using Shared.Protocol;
using Shared.Quests;
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
    private readonly RecipeRegistry _recipes;
    private readonly QuestTracker _quests;

    /// <summary>Поведение каждого предмета по номеру <see cref="ItemId"/>: связано с реестром предметов после загрузки модов.</summary>
    private readonly IItemBehavior[] _behaviors;

    /// <summary>События мира для модов: действия ставят их в очередь, обработчики вызываются после каждого сообщения.</summary>
    private readonly GameEvents _events;
    private readonly WorldEditor _editor;
    /// <summary>
    /// Сколько чанков отправлять одному игроку за тик. Иначе при входе клиент получил бы сотни чанков разом:
    /// и сервер сжимал бы их в одном тике, и клиент строил бы их меши в одном кадре.
    /// </summary>
    public const int MaxChunksPerTick = 16;

    /// <summary>Как часто сервер ищет чанки, рядом с которыми нет игроков: раз в секунду.</summary>
    private const int UnloadIntervalTicks = GameTime.TicksPerSecond;

    /// <summary>
    /// Сколько найденных ненужных чанков выгружается за тик. Выгрузка всех разом раз в секунду (в полёте — до 130 чанков)
    /// снимала свет до 30 мс в Debug и вместе с приходом чанков давала тик до 60 мс (замер после шага 8.7); пачками по 16
    /// очередь в полёте успевает разбираться (130 в секунду против 320).
    /// </summary>
    internal const int UnloadPerTick = 16;

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

    /// <summary>Тик мира — часы суток (<see cref="DayCycle"/>): растёт каждый тик, хранится в сохранении, T переводит на «час».</summary>
    private long _worldTime;
    private int _unloadedSinceLog;
    private readonly Queue<ChunkPos> _unloadQueue = new();

    public World World { get; }

    /// <summary>
    /// Сущности мира — всё, что движется и не является блоком (с этапа 7). Живут только на сервере; системы вызываются
    /// в <see cref="Tick"/> по порядку, удалённые убираются в его конце.
    /// </summary>
    internal EntityWorld Entities { get; } = new();

    private readonly EntitySync _entitySync = new();

    /// <summary>Чанки, чей свет изменился с прошлой рассылки (<see cref="SendLightChanges"/>); список переиспользуется.</summary>
    private readonly List<ChunkPos> _lightChanged = [];
    private readonly ItemSystem _itemSystem;
    private readonly EntityPersistence _entityPersistence;

    /// <summary>Разлёт добычи. Без сида: на исход игры он не влияет, тесты проверяют не точку, а что предмет есть.</summary>
    private readonly Random _random = new();

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
    /// <param name="mods">Моды из папки модов в порядке загрузки (<see cref="ModDiscovery.Discover"/>); null — без модов.</param>
    /// <exception cref="ContentLoadException">Ошибка в контенте игры или модов.</exception>
    /// <exception cref="ModLoadException">Ошибка в сборке мода или в его <see cref="IMod.Load"/>.</exception>
    public GameServer(int? seed, int? generatorThreads = null,
        int viewDistance = DefaultViewDistance, int verticalDistance = DefaultVerticalDistance,
        (int X, int Z)? spawnColumn = null, string? savePath = null, GameMode? defaultMode = null,
        IReadOnlyList<ModInfo>? mods = null)
        : this(seed, generatorThreads, viewDistance, verticalDistance, spawnColumn, savePath, defaultMode,
            mods ?? [], extraMods: [], Console.WriteLine)
    {
    }

    /// <param name="extraMods">Моды без папки, сразу экземплярами (тесты): загружаются после модов из <paramref name="mods"/>.</param>
    /// <param name="modLog">Куда пишут моды и ошибки их обработчиков (тесты перехватывают).</param>
    internal GameServer(int? seed, int? generatorThreads, int viewDistance, int verticalDistance,
        (int X, int Z)? spawnColumn, string? savePath, GameMode? defaultMode,
        IReadOnlyList<ModInfo> mods, IReadOnlyList<(string Id, IMod Mod)> extraMods, Action<string> modLog)
    {
        _viewDistance = viewDistance;
        _verticalDistance = verticalDistance;

        // Блоки и предметы — из JSON-контента игры и модов; клиент грузит те же файлы и сверяет имена при подключении.
        var content = GameContent.Load(mods);
        var registry = _registry = content.Blocks;
        _blocks = BaseBlocks.From(registry);
        _blockNames = [.. registry.All.Select(b => b.Name)];

        _items = content.Items;
        _itemSystem = new ItemSystem(_items);
        _itemNames = [.. _items.All.Select(i => i.Name)];
        _recipes = content.Recipes;
        _events = new GameEvents(modLog);
        _quests = new QuestTracker(content.Quests, _events);
        _behaviors = LoadMods(content, mods, extraMods, _events, modLog);

        if (savePath != null)
            _save = WorldSave.Open(savePath, registry, _items);
        try
        {
            var isNew = _save?.Info == null;
            World = new World(registry, _blocks, seed ?? Random.Shared.Next(), generatorThreads ?? ChunkGenerator.DefaultWorkerCount,
                spawnColumn ?? default, _save, defaultMode ?? GameMode.Survival);
            if (_save != null)
            {
                LogOpened(isNew, seed, spawnColumn, defaultMode);
                RecordMods(_save, mods);
            }
        }
        catch
        {
            _save?.Dispose();
            throw;
        }

        _editor = new WorldEditor(this);
        _entityPersistence = new EntityPersistence(Entities, _save, _items);
        // Мир прошлой версии (без времени в сохранении) и новый мир начинают утром.
        _worldTime = _save?.LoadTime() ?? DayCycle.NewWorldTime;
    }

    /// <summary>Тик мира (часы суток). Для тестов.</summary>
    internal long WorldTime => _worldTime;

    /// <summary>
    /// Подключает моды: каждый регистрирует поведения и подписки через свой <see cref="IModContext"/>. Первым — встроенный
    /// <c>base</c>, затем остальные по порядку; после этого поведения связываются с предметами, подписки закрываются.
    /// </summary>
    private static IItemBehavior[] LoadMods(ContentRegistries content, IReadOnlyList<ModInfo> mods,
        IReadOnlyList<(string Id, IMod Mod)> extraMods, GameEvents events, Action<string> log)
    {
        var behaviors = new ItemBehaviorRegistry();
        var loaded = new List<(string Id, IMod Mod)> { (BaseMod.Id, new BaseMod()) };
        foreach (var info in mods)
        {
            // Мод только из данных (без сборки) кода не имеет: его контент уже в реестрах.
            if (ModAssemblies.CreateMod(info) is { } mod)
                loaded.Add((info.Id, mod));
        }
        loaded.AddRange(extraMods);

        foreach (var (id, mod) in loaded)
        {
            try
            {
                mod.Load(new ModContext(id, content, behaviors, events, log));
            }
            catch (Exception exception) when (exception is not ModLoadException)
            {
                throw new ModLoadException($"Мод '{id}': ошибка в IMod.Load: {exception.Message}", exception);
            }
        }
        events.Freeze();

        if (mods.Count > 0)
            Console.WriteLine($"[Server] Моды: {string.Join(", ", mods.Select(m => m.AssemblyFile is null ? $"{m} (только данные)" : m.ToString()))}");
        return behaviors.Bind(content.Items);
    }

    /// <summary>
    /// Сверяет моды с теми, с которыми мир открывали прошлый раз, и запоминает нынешние. Пропавший мод — повод
    /// предупредить: его блоки станут воздухом, предметы пропадут (это уже пишет таблица имён сохранения).
    /// </summary>
    private static void RecordMods(WorldSave save, IReadOnlyList<ModInfo> mods)
    {
        var current = mods.ToDictionary(mod => mod.Id, mod => mod.Version.ToString(), StringComparer.Ordinal);
        foreach (var (id, version) in save.LoadModList())
        {
            if (!current.ContainsKey(id))
                Console.WriteLine($"[Server] Мир открывали с модом '{id}' {version}, сейчас его нет: его блоки станут воздухом, предметы пропадут");
        }
        save.SaveModList(current);
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
        // Часы мира — в начале тика: всё, что сервер шлёт за тик (JoinAccepted, TimeSync), несёт одно время, и клиент,
        // прибавляющий по тику, совпадает с ним.
        _worldTime++;

        while (_pendingConnections.TryDequeue(out var connection))
            _sessions.Add(new ClientSession(connection));

        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                session.Movement.Refill();
            while (session.Connection.TryReceive(out var message))
            {
                Handle(session, message);
                // Обработчики модов — после того, как действие игрока закончено целиком.
                _events.Dispatch();
            }
        }

        // Ломание удержанием: по тикам сервера, а не по сообщениям клиента — ломать быстрее положенного нельзя.
        foreach (var session in _sessions)
        {
            if (session.IsJoined && session.Breaking != null)
                ContinueBreaking(session);
        }
        _events.Dispatch();

        // Системы сущностей — здесь, после действий игроков, по порядку. Притяжение задаёт скорость до движения,
        // подбор и слияние смотрят на положения после него. Рассылка сущностей — после рассылки чанков,
        // удалённые убираются в конце тика (EntityWorld.FlushDestroyed).
        // Сущность игрока стоит там же, где его принятая позиция; здоровье восстанавливается.
        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                Entities.Get<Position>(session.Entity).Value = session.PlayerPosition;
        }
        Regenerate();
        _itemSystem.Age(Entities);
        _itemSystem.Attract(Entities, _sessions);
        MovementSystem.Run(Entities, World.Chunks, _registry);
        _itemSystem.PickUp(Entities, _sessions, (session, stack) => _events.Raise(new ItemPickedUp(_editor, session, stack)));
        _itemSystem.Merge(Entities);
        _events.Dispatch();

        World.CollectGenerated();
        while (World.TryTakeLoadedEntities(out var loadedChunk, out var entityData))
            _entityPersistence.Restore(loadedChunk, entityData);
        // Свет, изменённый действиями и пришедшими чанками, — до рассылки чанков: новые чанки и так несут свежий свет.
        SendLightChanges();
        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                StreamChunks(session);
        }
        _entitySync.Run(Entities, _sessions);

        ++_tick;
        UnloadUnusedChunks();
        if (_tick % AutosaveIntervalTicks == 0)
            Autosave();

        // Задания «иметь предмет» — по инвентарю в конце тика: что бы его ни меняло (добыча, ремесло, клики, моды), оно уже учтено.
        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                _quests.CheckObtain(session, _editor);
        }
        _events.Dispatch();

        // Свет, изменённый после рассылки чанков (выгрузка, обработчики модов), — в этом же тике.
        SendLightChanges();

        // Изменения инвентаря за тик — одним сообщением, в конце: что бы ни меняло инвентарь в этом тике, оно уже учтено.
        foreach (var session in _sessions)
        {
            if (session.IsJoined && session.Inventory.TakeChanges() is { IsEmpty: false } changes)
                session.Connection.Send(new InventoryChanged(changes));
        }

        // Сверка часов с клиентами — раз в 10 с, между сверками они считают сами.
        if (_tick % TimeSync.IntervalTicks == 0)
            BroadcastTime();

        // Последним: до конца тика номер удалённой сущности не переиспользуется, и всё, что в тике её удалило, уже прошло.
        Entities.FlushDestroyed();
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
                    session.Inventory.SelectedSlot, session.Inventory.CopySlots(), session.Quests, HealthOf(session).Current));
            }
        }
        _entityPersistence.SaveAll();
        _save.SaveTime(_worldTime);
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

    /// <summary>
    /// Шлёт весь свет каждого чанка, чей свет изменился, игрокам, у которых этот чанк есть. Остальные получат свет
    /// вместе с чанком (<see cref="ChunkData"/>). Чанк сжимается один раз на всех игроков.
    /// </summary>
    private void SendLightChanges()
    {
        World.Light.TakeChanged(_lightChanged);
        foreach (var position in _lightChanged)
        {
            // Свет соседей меняется и при выгрузке чанка; самого выгруженного уже нет.
            if (World.Chunks.Get(position) is not { } chunk)
                continue;
            ChunkLightChanged? message = null;
            foreach (var session in _sessions)
            {
                if (session.IsJoined && session.View.WasSent(position))
                    session.Connection.Send(message ??= ChunkLightChanged.From(chunk));
            }
        }
        _lightChanged.Clear();
    }

    /// <summary>
    /// Выгружает из памяти сервера чанки, которые не нужны ни одному игроку: раз в секунду ищет их (когда прошлые уже
    /// разобраны), а выгружает по <see cref="UnloadPerTick"/> за тик — те, что всё ещё не нужны (игрок мог вернуться).
    /// </summary>
    private void UnloadUnusedChunks()
    {
        bool IsNeeded(ChunkPos position) => _sessions.Any(s => s.IsJoined && s.View.Keeps(position));

        // Сводка раз в 10 секунд, а не каждую выгрузку: в полёте выгрузка идёт каждый тик.
        if (_tick % (UnloadIntervalTicks * 10) == 0 && _unloadedSinceLog > 0)
        {
            Console.WriteLine($"[Server] За 10 с выгружено {_unloadedSinceLog} чанков, в памяти {World.Chunks.Count}; " +
                $"с запуска загружено из сохранения {World.LoadedCount}, сгенерировано {World.GeneratedCount}");
            _unloadedSinceLog = 0;
        }

        // По столбцам и полосам (X, Z, Y): соседи попадают в одну пачку, и свет на сторонах между уходящими не пересчитывается
        // (World.Unload). В порядке словаря пачки были вразброс, и выгрузка стоила вдвое дороже.
        if (_tick % UnloadIntervalTicks == 0 && _unloadQueue.Count == 0)
        {
            foreach (var position in World.Chunks.All.Select(c => c.Position).Where(p => !IsNeeded(p))
                         .OrderBy(p => p.X).ThenBy(p => p.Z).ThenBy(p => p.Y))
                _unloadQueue.Enqueue(position);
        }
        if (_unloadQueue.Count == 0)
            return;

        var batch = new List<ChunkPos>(UnloadPerTick);
        while (batch.Count < UnloadPerTick && _unloadQueue.TryDequeue(out var position))
        {
            if (!IsNeeded(position))
                batch.Add(position);
        }
        var unloaded = World.Unload(batch);
        // Сущности уходят вместе со своими чанками: записываются и убираются из памяти.
        _entityPersistence.Save(unloaded, remove: true);
        _unloadedSinceLog += unloaded.Count;
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
        // Погибший до возрождения ничего не делает: сообщения, отправленные до смерти или с экрана смерти, молча пропускаются.
        if (session.IsDead && message is not Respawn)
            return;

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

            case AdvanceTime when session.IsJoined:
                // Прав доступа пока нет, а игра одиночная: перевести время может любой игрок.
                _worldTime += DayCycle.HourTicks;
                BroadcastTime();
                Console.WriteLine($"[Server] {session.PlayerName}: время +1/24 суток, {DayCycle.Describe(_worldTime)}");
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
                ReturnCursorOrThrow(session);
                break;

            case InventoryDragDistribute drag when session.IsJoined:
                if (!session.Inventory.DistributeCursor(drag.TargetSlots, drag.OnePerSlot))
                    Console.WriteLine($"[Server] {session.PlayerName}: неверные цели перетаскивания инвентаря");
                break;

            case CraftRecipe craft when session.IsJoined:
                HandleCraft(session, craft);
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

            case StopBreaking when session.IsJoined:
                session.Breaking = null;
                break;

            case DropItem drop when session.IsJoined:
                HandleDrop(session, drop.WholeStack);
                break;

            case Respawn when session.IsJoined:
                HandleRespawn(session);
                break;

            default:
                // До JoinRequest принимается только он сам. Незнакомое сообщение — ошибка протокола, а не повод падать.
                Console.WriteLine($"[Server] Сообщение {message.GetType().Name} отклонено (joined: {session.IsJoined})");
                break;
        }
    }

    /// <summary>Q: одна штука из руки (или вся стопка) летит предметом вперёд по взгляду (<see cref="ItemEntities.Throw"/>). Пустая рука — ничего.</summary>
    private void HandleDrop(ClientSession session, bool wholeStack)
    {
        var slot = session.Inventory.SelectedSlot;
        var held = session.Inventory[slot];
        if (held.IsEmpty)
            return;

        var count = wholeStack ? held.Count : 1;
        session.Inventory.Remove(slot, count);
        Throw(session, new ItemStack(held.Item, count));
    }

    /// <summary>
    /// Экран инвентаря закрыт: предмет с курсора возвращается в слоты, а что не влезло, выбрасывается перед игроком —
    /// иначе оно осталось бы на невидимом курсоре.
    /// </summary>
    private void ReturnCursorOrThrow(ClientSession session)
    {
        session.Inventory.ReturnCursor();
        var left = session.Inventory.Cursor;
        if (left.IsEmpty)
            return;
        session.Inventory.Set(Inventory.CursorSlot, ItemStack.Empty);
        Throw(session, left);
    }

    private void Throw(ClientSession session, ItemStack stack)
    {
        var (position, velocity) = ItemEntities.Throw(session.PlayerPosition, session.Look);
        ItemEntities.Spawn(Entities, position, velocity, stack, ItemEntities.ThrowPickupDelay);
    }

    /// <summary>
    /// Игрок, который уже был в этом мире, продолжает с того места, где вышел: с тем же взглядом, режимом и инвентарём.
    /// Новый появляется в точке появления, в режиме мира и с пустым инвентарём.
    /// </summary>
    private void HandleJoin(ClientSession session, JoinRequest join)
    {
        var saved = _save?.LoadPlayer(join.PlayerName);
        var start = saved != null && IsValidPosition(saved.Position) ? saved.Position : World.SpawnPosition;
        // Погиб и вышел, не возродившись: вещи уже выпали, при входе — возрождение в точке появления.
        var health = saved?.Health is { } savedHealth ? Math.Min(savedHealth, PlayerHealth.Max) : PlayerHealth.Max;
        if (health <= 0)
            (start, health) = (World.SpawnPosition, PlayerHealth.Max);
        var mode = saved?.Mode ?? World.DefaultMode;
        var inventory = new Inventory(_items);
        if (saved != null)
            RestoreInventory(inventory, saved);

        session.Join(_nextPlayerId++, join.PlayerName, mode, inventory, saved?.Quests ?? new QuestBook(),
            new PlayerChunkView(_viewDistance, _verticalDistance, BlockPos.Containing(start).Chunk));
        // До первого PlayerMove игрок стоит в начальной точке, иначе его действия сочлись бы «издалека».
        session.PlayerPosition = start;
        session.Server = this;
        session.Entity = Entities.Create();
        Entities.Set(session.Entity, new Position { Value = start });
        Entities.Set(session.Entity, new Health { Current = health, Max = PlayerHealth.Max });
        Entities.Set(session.Entity, new Fall { HighestY = start.Y });
        session.Look = saved?.Look ?? default;
        // Чанки придут в следующих тиках (StreamChunks), начиная с ближайших.
        session.Connection.Send(new JoinAccepted(session.PlayerId, start, session.Look, mode, _blockNames, _itemNames, session.View.LoadArea,
            _worldTime));
        // Инвентарь уходит целиком, поэтому изменения от его заполнения отправлять не нужно.
        session.Connection.Send(new InventoryContents(inventory.CopySlots(), inventory.SelectedSlot));
        inventory.TakeChanges();
        session.Connection.Send(new QuestBookContents([.. session.Quests.Completed],
            [.. session.Quests.Progress.Select(entry => new QuestCount(entry.Key, entry.Value))]));
        SendHealth(session);
        Console.WriteLine($"[Server] {session.PlayerName} подключился (id {session.PlayerId}, режим {mode.ToName()})");
        _events.Raise(new PlayerJoined(_editor, session));
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
    private void ChangeMode(ClientSession session, GameMode mode)
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
            TrackFall(session);
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

    /// <summary>Задания игрока на сервере. Для тестов: менять можно только между тиками, из потока теста.</summary>
    internal QuestBook PlayerQuests(int playerId) => _sessions.Single(s => s.IsJoined && s.PlayerId == playerId).Quests;

    /// <summary>Мир сервера так, как его видят моды, и игрок от лица модов. Для тестов: только между тиками, из потока теста.</summary>
    internal IWorld ModWorld => _editor;
    internal IPlayer ModPlayer(int playerId) => _sessions.Single(s => s.IsJoined && s.PlayerId == playerId);

    /// <summary>Переносит клиента в <see cref="ClientSession.PlayerPosition"/>; перенос — не падение, высота считается заново.</summary>
    private void SendTeleport(ClientSession session)
    {
        Entities.Get<Fall>(session.Entity).HighestY = session.PlayerPosition.Y;
        session.Connection.Send(new PlayerTeleport(session.PlayerPosition, session.Movement.NextTeleport()));
    }

    internal ref Health HealthOf(ClientSession session) => ref Entities.Get<Health>(session.Entity);

    private void SendHealth(ClientSession session)
    {
        var health = HealthOf(session);
        session.Connection.Send(new HealthChanged(health.Current, health.Max));
    }

    /// <summary>
    /// Падение по принятым позициям: запоминается самая высокая точка ног, а при касании земли урон — по высоте падения
    /// (<see cref="PlayerHealth.FallDamage"/>). В творческом урона нет — это решает <see cref="Damage(ClientSession, int, string)"/>.
    /// </summary>
    private void TrackFall(ClientSession session)
    {
        ref var fall = ref Entities.Get<Fall>(session.Entity);
        var y = session.PlayerPosition.Y;
        fall.HighestY = Math.Max(fall.HighestY, y);
        // На земле — если чуть ниже ног твёрдый блок.
        if (!PlayerPhysics.Collides(session.PlayerPosition - new Vector3d(0, 0.01, 0), World.Chunks, _registry))
            return;

        var damage = PlayerHealth.FallDamage(fall.HighestY - y);
        fall.HighestY = y;
        if (damage > 0)
            Damage(session, damage, PlayerHealth.FallCause);
    }

    /// <summary>Урон игроку в выживании: здоровье, сообщение клиенту, событие модов; до нуля — смерть.</summary>
    internal void Damage(ClientSession session, int amount, string cause)
    {
        if (amount <= 0 || session.IsDead || session.Mode != GameMode.Survival)
            return;

        ref var health = ref HealthOf(session);
        health.Current = Math.Max(0, health.Current - amount);
        health.RegenTicks = 0;
        SendHealth(session);
        _events.Raise(new PlayerDamaged(_editor, session, amount, cause));
        if (health.Current == 0)
            Die(session, cause);
    }

    /// <summary>
    /// Смерть: весь инвентарь и курсор выпадают предметами на месте (их можно подобрать, вернувшись), клиент получает
    /// экран смерти, действия игрока до возрождения не принимаются.
    /// </summary>
    private void Die(ClientSession session, string cause)
    {
        session.IsDead = true;
        session.Breaking = null;
        var center = session.PlayerPosition + new Vector3d(0, 1, 0);
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
        {
            var stack = session.Inventory[slot];
            if (stack.IsEmpty)
                continue;
            session.Inventory.Set(slot, ItemStack.Empty);
            var velocity = new Vector3((_random.NextSingle() * 2 - 1) * 2, 3, (_random.NextSingle() * 2 - 1) * 2);
            ItemEntities.Spawn(Entities, center, velocity, stack, ItemEntities.ThrowPickupDelay);
        }

        session.Connection.Send(new DeathNotice(cause));
        Console.WriteLine($"[Server] {session.PlayerName} погиб ({cause})");
        _events.Raise(new PlayerDied(_editor, session, cause));
    }

    /// <summary>«Возродиться»: в точку появления мира с полным здоровьем.</summary>
    private void HandleRespawn(ClientSession session)
    {
        if (!session.IsDead)
            return;

        session.IsDead = false;
        ref var health = ref HealthOf(session);
        (health.Current, health.RegenTicks) = (health.Max, 0);
        session.PlayerPosition = World.SpawnPosition;
        SendTeleport(session);
        SendHealth(session);
    }

    /// <summary>Восстановление здоровья: единица за <see cref="PlayerHealth.RegenIntervalTicks"/> у живых игроков.</summary>
    private void Regenerate()
    {
        foreach (var session in _sessions)
        {
            if (!session.IsJoined || session.IsDead)
                continue;
            ref var health = ref HealthOf(session);
            if (health.Current >= health.Max)
            {
                health.RegenTicks = 0;
                continue;
            }
            if (++health.RegenTicks < PlayerHealth.RegenIntervalTicks)
                continue;
            health.RegenTicks = 0;
            health.Current++;
            SendHealth(session);
        }
    }

    /// <summary>Здоровье игрока. Для тестов.</summary>
    internal int PlayerHealthOf(int playerId) => HealthOf(_sessions.Single(s => s.IsJoined && s.PlayerId == playerId)).Current;

    /// <summary>Урон игроку по номеру. Для тестов (позже — для API модов).</summary>
    internal void Damage(int playerId, int amount, string cause) =>
        Damage(_sessions.Single(s => s.IsJoined && s.PlayerId == playerId), amount, cause);

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
        var target = VoxelRaycast.Cast(World.Chunks, _registry, use.EyePosition, use.LookDirection, GameRules.BlockReach);
        _behaviors[session.Inventory.Selected.Item.Value].Use(use.Action,
            new ItemUseContext(_editor, _items, session, target, use.EyePosition, new Breaker(this, session)));
    }

    /// <summary>
    /// Ремесло: тратит ингредиенты и кладёт результат, если всё это есть, станок рядом с глазом и результату хватает места.
    /// Shift+клик повторяет, пока получается. Невыполнимая просьба ничего не меняет: клиент мог показать рецепт по
    /// чуть устаревшему инвентарю (или отойти от станка), это не ошибка протокола.
    /// </summary>
    private void HandleCraft(ClientSession session, CraftRecipe craft)
    {
        if (craft.Recipe is null || !_recipes.TryGet(craft.Recipe, out var recipe))
        {
            Console.WriteLine($"[Server] {session.PlayerName}: неизвестный рецепт '{craft.Recipe}'");
            return;
        }

        var eye = session.PlayerPosition + new Vector3d(0, PlayerPhysics.EyeHeight, 0);
        if (recipe.Station is { } station && !Crafting.IsStationNear(World.Chunks, eye, station))
            return;

        // Каждый раз тратит хотя бы одну штуку, поэтому повторов не больше, чем предметов в инвентаре.
        var times = 0;
        while (session.Inventory.TryExchange(recipe.Ingredients, recipe.Result))
        {
            times++;
            if (!craft.All)
                break;
        }

        if (times > 0)
        {
            _quests.Count(session, QuestGoalKind.Craft, goal => goal.Item == recipe.Result.Item, recipe.Result.Count * times, _editor);
            _events.Raise(new ItemCrafted(_editor, session, recipe, times));
        }
    }

    /// <summary>
    /// Начинает ломание блока игроком (<see cref="IBlockBreaker"/>): запоминает цель, тик и нужное число тиков по твёрдости
    /// и инструменту в руке. В творческом режиме и у блока с нулевой твёрдостью ломает сразу.
    /// </summary>
    private void StartBreaking(ClientSession session, BlockPos pos)
    {
        var block = World.Chunks.GetBlock(pos);
        if (block.IsAir)
        {
            session.Breaking = null;
            return;
        }

        var held = session.Inventory.Selected.Item;
        var ticks = session.Mode == GameMode.Creative ? 0 : Mining.BreakTicks(_registry.Get(block).Mining, _items.Get(held).Tool);
        session.Breaking = new BlockBreaking(pos, block, _tick, ticks, session.Inventory.SelectedSlot, held);
        if (ticks == 0)
            FinishBreaking(session);
    }

    /// <summary>
    /// Тик ломания: отменяет его, если блок уже не тот, игрок сменил предмет или отошёл дальше досягаемости; завершает,
    /// когда прошло нужное число тиков.
    /// </summary>
    private void ContinueBreaking(ClientSession session)
    {
        var breaking = session.Breaking!.Value;
        var eye = session.PlayerPosition + new Vector3d(0, PlayerPhysics.EyeHeight, 0);
        var center = new Vector3d(breaking.Position.X + 0.5, breaking.Position.Y + 0.5, breaking.Position.Z + 0.5);
        if (World.Chunks.GetBlock(breaking.Position) != breaking.Block
            || session.Inventory.SelectedSlot != breaking.Slot || session.Inventory.Selected.Item != breaking.Held
            // Луч до любой точки блока не длиннее BlockReach: до центра — не дальше ещё половины диагонали куба.
            || Vector3d.Distance(eye, center) > GameRules.BlockReach + 0.87)
        {
            session.Breaking = null;
            return;
        }

        if (_tick - breaking.StartTick >= breaking.Ticks)
            FinishBreaking(session);
    }

    /// <summary>
    /// Ломает блок и в выживании кладёт добычу, если инструмент позволяет её получить. Если добыча положена, а класть
    /// некуда, блок не ломается: предметов на земле пока нет, и добыча пропала бы молча. Без добычи (рука по камню)
    /// блок ломается всегда.
    /// </summary>
    private void FinishBreaking(ClientSession session)
    {
        var breaking = session.Breaking!.Value;
        session.Breaking = null;

        if (session.Mode == GameMode.Creative)
        {
            _editor.TryBreakBlock(breaking.Position, session);
            return;
        }

        // Блок ломается всегда, добыча падает предметом (с шага 7.3) — даже при полном инвентаре она не пропадает.
        if (!_editor.TryBreakBlock(breaking.Position, session))
            return;
        var drop = _items.ForBlock(breaking.Block);
        if (!drop.IsAir && Mining.CanHarvest(_registry.Get(breaking.Block).Mining, _items.Get(breaking.Held).Tool))
            ItemEntities.SpawnDrop(Entities, breaking.Position, new ItemStack(drop, 1), _random);
    }

    /// <summary>Ломание от имени игрока для поведений предметов (<see cref="ItemUseContext.Breaker"/>).</summary>
    private sealed class Breaker(GameServer server, ClientSession session) : IBlockBreaker
    {
        public void StartBreaking(BlockPos pos) => server.StartBreaking(session, pos);
    }

    private static bool IsValidPosition(Vector3d position) =>
        position.IsFinite
        && Math.Abs(position.X) < MaxCoordinate && Math.Abs(position.Y) < MaxCoordinate && Math.Abs(position.Z) < MaxCoordinate;

    /// <summary>Сверка часов: тик мира — всем вошедшим.</summary>
    private void BroadcastTime()
    {
        var sync = new TimeSync(_worldTime);
        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                session.Connection.Send(sync);
        }
    }

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
    private sealed class WorldEditor(GameServer server) : IWorld
    {
        public BlockId GetBlock(BlockPos pos) => server.World.Chunks.GetBlock(pos);

        public LightLevel GetLight(BlockPos pos) => new(server.World.Light.GetSky(pos), server.World.Light.GetBlock(pos));

        public WorldTime Time => new(server._worldTime);

        public bool IsBlockedByPlayer(BlockPos pos, BlockId block)
        {
            if (!server._registry.IsSolid(block))
                return false;

            // По коробам формы: полублок, поставленный под ноги парящему над ним игроку, его не задевает.
            var boxes = server._registry.ShapeOf(block).Boxes;
            foreach (var session in server._sessions)
            {
                if (!session.IsJoined)
                    continue;
                var body = PlayerPhysics.BodyAt(session.PlayerPosition);
                foreach (var box in boxes)
                {
                    if (body.Intersects(Aabb.Of(pos, box)))
                        return true;
                }
            }
            return false;
        }

        public bool TryBreakBlock(BlockPos pos, IPlayer player)
        {
            var old = GetBlock(pos);
            if (old.IsAir || !TrySetBlock(pos, BlockId.Air))
                return false;

            server._events.Raise(new BlockBroken(this, player, pos, old));
            if (player is ClientSession session)
                server._quests.Count(session, QuestGoalKind.Break, goal => goal.Block == old, 1, this);
            return true;
        }

        public void SpawnItem(Vector3d position, ItemStack stack)
        {
            if (!position.IsFinite || !server._items.Contains(stack.Item))
                return;
            var max = server._items.Get(stack.Item).MaxStackSize;
            // Больше предела — несколькими предметами: на земле, как в инвентаре, стопка не больше своего предела.
            // Пустая стопка (0 штук) не даёт ни одного.
            for (var left = stack.Count; left > 0; left -= max)
                ItemEntities.Spawn(server.Entities, position, Vector3.Zero, new ItemStack(stack.Item, Math.Min(left, max)), ItemEntities.DropPickupDelay);
        }

        public bool TryPlaceBlock(BlockPos pos, BlockId block, IPlayer player)
        {
            // Факел и другие блоки с опорой ставятся только на твёрдый блок с полным верхом.
            if (server._registry.Get(block).NeedsSupport && !server._registry.CanSupport(GetBlock(pos + Down)))
                return false;
            if (!TrySetBlock(pos, block))
                return false;

            server._events.Raise(new BlockPlaced(this, player, pos, block));
            if (player is ClientSession session)
                server._quests.Count(session, QuestGoalKind.Place, goal => goal.Block == block, 1, this);
            return true;
        }

        public bool TrySetBlock(BlockPos pos, BlockId block)
        {
            if (!server.World.TrySetBlock(pos, block))
                return false;

            server.BroadcastBlockChange(new BlockChanged(pos, block));
            BreakUnsupported(pos);
            return true;
        }

        private static readonly BlockPos Up = new(0, 1, 0);
        private static readonly BlockPos Down = new(0, -1, 0);

        /// <summary>
        /// Блок над <paramref name="below"/>, стоящий на опоре (<see cref="BlockDefinition.NeedsSupport"/>), ломается
        /// с добычей, если опора больше не держит (<see cref="BlockRegistry.CanSupport"/>: сломали или заменили на плиту), —
        /// кто бы её ни сменил: игрок, мод, правило. Без событий и заданий:
        /// это не действие игрока. Вверх по столбику — циклом: у модов блок с опорой может быть твёрдым и сам держать
        /// такой же, и рекурсия по высокому столбу переполнила бы стек.
        /// </summary>
        private void BreakUnsupported(BlockPos below)
        {
            while (!server._registry.CanSupport(GetBlock(below)))
            {
                var above = below + Up;
                var block = GetBlock(above);
                if (block.IsAir || !server._registry.Get(block).NeedsSupport || !server.World.TrySetBlock(above, BlockId.Air))
                    return;

                server.BroadcastBlockChange(new BlockChanged(above, BlockId.Air));
                var drop = server._items.ForBlock(block);
                if (!drop.IsAir)
                    ItemEntities.SpawnDrop(server.Entities, above, new ItemStack(drop, 1), server._random);
                below = above;
            }
        }
    }
}
