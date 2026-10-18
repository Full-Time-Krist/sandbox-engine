using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Shared;
using Shared.Blocks;
using Shared.Items;
using Shared.Voxels;

namespace Server.Storage;

/// <summary>Неизменные свойства мира, которые записываются при его создании.</summary>
/// <param name="DefaultMode">Режим нового игрока в этом мире (<c>--mode</c> при создании).</param>
internal sealed record WorldInfo(int Seed, Vector3d SpawnPosition, GameMode DefaultMode = GameMode.Survival);

/// <summary>Игрок в сохранении.</summary>
/// <param name="Mode">Режим; null — не записан (игрок из сохранения версии 1), тогда берётся режим мира по умолчанию.</param>
/// <param name="Slots">Все слоты инвентаря и курсор (<see cref="Inventory.SlotCount"/> штук), в id реестра этого запуска.</param>
internal sealed record SavedPlayer(Vector3d Position, LookAngles Look, GameMode? Mode, int SelectedSlot, ImmutableArray<ItemStack> Slots);

/// <summary>
/// Сохранение мира: один файл SQLite. Таблицы <c>meta</c> (версия формата, сид, точка появления, режим по умолчанию),
/// <c>block_names</c> и <c>item_names</c> (номер блока или предмета в сохранении → имя, см. <see cref="NameIdMap"/>),
/// <c>chunks</c> (позиция → сжатые блоки в формате <see cref="ChunkCodec"/>) и <c>players</c> (имя → позиция,
/// взгляд, режим, выбранный слот, инвентарь в формате <see cref="InventoryCodec"/>).
/// </summary>
/// <remarks>
/// <para>
/// Запись идёт в отдельном потоке: тик сервера и фоновые потоки генерации только ставят данные в очередь
/// и не ждут диск. Поток записи пишет накопившееся одной транзакцией: так быстрее, а сбой посреди записи
/// не оставит в файле половину пачки.
/// </para>
/// <para>
/// Чанк, поставленный в очередь, но ещё не записанный, лежит в памяти (<see cref="_pendingChunks"/>), и загрузка
/// сначала ищет его там. Иначе быстро выгруженный и снова запрошенный чанк прочитался бы с диска в старом виде,
/// а то и сгенерировался бы заново, и изменения пропали бы. Из памяти чанк убирается только после записи,
/// и только если за это время не пришла его более новая версия.
/// </para>
/// <para>
/// Читать (<see cref="TryLoadChunk"/>, <see cref="LoadPlayer"/>) и ставить в очередь можно из любого потока:
/// у каждого потока своё соединение для чтения. Режим WAL позволяет читать, пока поток записи пишет.
/// </para>
/// </remarks>
internal sealed class WorldSave : IDisposable
{
    /// <summary>
    /// Версия схемы базы. Формат блоков чанка и инвентаря версионируется отдельно, первым байтом данных.
    /// Версия 2 (этап 3): таблица <c>item_names</c>, у игрока взгляд, режим, выбранный слот и инвентарь.
    /// </summary>
    public const int FormatVersion = 2;

    /// <summary>
    /// Столбцы игрока, появившиеся в версии 2. Новая база создаётся сразу с ними, у базы версии 1 их добавляет
    /// обновление (<see cref="UpgradeFrom1"/>). Значения по умолчанию — для игроков, записанных версией 1.
    /// </summary>
    private static readonly string[] PlayerColumnsAddedIn2 =
    [
        "yaw REAL NOT NULL DEFAULT 0",
        "pitch REAL NOT NULL DEFAULT 0",
        "mode TEXT",
        "selected_slot INTEGER NOT NULL DEFAULT 0",
        "inventory BLOB",
    ];

    /// <summary>Сколько записей поток записи кладёт в одну транзакцию.</summary>
    private const int MaxBatch = 512;

    private readonly string _connectionString;
    private readonly SqliteConnection _writeConnection;
    private readonly ThreadLocal<SqliteConnection> _readConnections;
    private readonly BlockingCollection<WriteItem> _queue = new(new ConcurrentQueue<WriteItem>());
    private readonly ConcurrentDictionary<ChunkPos, byte[]> _pendingChunks = new();
    private readonly ConcurrentDictionary<string, PlayerWrite> _pendingPlayers = new(StringComparer.Ordinal);
    private readonly ItemRegistry _items;
    private Thread? _writer;
    private volatile Exception? _fault;

    /// <summary>Путь к файлу базы.</summary>
    public string Path { get; }

    /// <summary>Сид и точка появления. null — мир новый, их ещё нужно записать (<see cref="Initialize"/>).</summary>
    public WorldInfo? Info { get; private set; }

    public BlockIdMap BlockIds { get; }

    public NameIdMap ItemIds { get; }

    /// <summary>С какой версии схемы сохранение обновлено при открытии, или null, если обновлять не пришлось.</summary>
    public int? UpgradedFrom { get; }

    private WorldSave(string path, BlockRegistry registry, ItemRegistry items)
    {
        Path = path;
        _items = items;
        // Pooling=False: иначе пул держит файл открытым и после Dispose, и папку мира нельзя удалить.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        _writeConnection = OpenConnection();
        try
        {
            Execute(_writeConnection, $"""
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value);
                CREATE TABLE IF NOT EXISTS block_names (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
                CREATE TABLE IF NOT EXISTS item_names (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
                CREATE TABLE IF NOT EXISTS chunks (
                    x INTEGER NOT NULL, y INTEGER NOT NULL, z INTEGER NOT NULL, data BLOB NOT NULL,
                    PRIMARY KEY (x, y, z)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS players (name TEXT PRIMARY KEY, x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL,
                    {string.Join(", ", PlayerColumnsAddedIn2)});
                """);

            // Версия записывается вместе со свойствами мира (Initialize); у новой базы её ещё нет, и таблицы уже новые.
            var version = ReadMeta(_writeConnection, "format_version") is { } stored ? Convert.ToInt64(stored) : (long?)null;
            if (version > FormatVersion)
                throw new InvalidDataException($"Мир сохранён более новой версией игры (формат {version}, поддерживается до {FormatVersion}).");
            if (version == 1)
            {
                UpgradeFrom1();
                UpgradedFrom = 1;
            }
            if (version != null)
                Info = ReadInfo();

            BlockIds = new BlockIdMap(ReadNames("block_names"), registry);
            AppendNames("block_names", BlockIds.NewNames);
            ItemIds = new NameIdMap("предметов", ReadNames("item_names"), [.. items.All.Select(i => i.Name)]);
            AppendNames("item_names", ItemIds.NewNames);
        }
        catch
        {
            _writeConnection.Dispose();
            throw;
        }

        _readConnections = new ThreadLocal<SqliteConnection>(OpenConnection, trackAllValues: true);
    }

    /// <summary>Открывает сохранение или создаёт новое (вместе с папкой).</summary>
    /// <param name="startWriter">false — поток записи не запускается (<see cref="StartWriter"/>). Только для тестов очереди.</param>
    /// <exception cref="InvalidDataException">Файл от более новой версии игры или испорчен.</exception>
    public static WorldSave Open(string path, BlockRegistry registry, ItemRegistry items, bool startWriter = true)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);

        var save = new WorldSave(path, registry, items);
        if (startWriter)
            save.StartWriter();
        return save;
    }

    /// <summary>Записывает свойства нового мира. Только один раз и до игры.</summary>
    public void Initialize(WorldInfo info)
    {
        if (Info != null)
            throw new InvalidOperationException("Мир уже создан");

        // Своё соединение: соединение записи уже принадлежит потоку записи, а SQLite-соединение — одному потоку за раз.
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        WriteMeta("format_version", FormatVersion);
        WriteMeta("seed", info.Seed);
        WriteMeta("spawn_x", info.SpawnPosition.X);
        WriteMeta("spawn_y", info.SpawnPosition.Y);
        WriteMeta("spawn_z", info.SpawnPosition.Z);
        WriteMeta("default_mode", info.DefaultMode.ToName());
        transaction.Commit();
        Info = info;

        void WriteMeta(string key, object value)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value)";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Моды (id → версия), с которыми мир открывали прошлый раз; пусто у нового мира и у мира без модов.</summary>
    public IReadOnlyDictionary<string, string> LoadModList()
    {
        using var connection = OpenConnection();
        return ReadMeta(connection, "mods") is string json
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []
            : new Dictionary<string, string>();
    }

    /// <summary>Запоминает моды (id → версия), с которыми открыт мир. Как и <see cref="Initialize"/> — до игры.</summary>
    public void SaveModList(IReadOnlyDictionary<string, string> mods)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('mods', $value)";
        command.Parameters.AddWithValue("$value", System.Text.Json.JsonSerializer.Serialize(mods));
        command.ExecuteNonQuery();
    }

    internal void StartWriter()
    {
        _writer = new Thread(WriteLoop) { Name = "WorldSave", IsBackground = true };
        _writer.Start();
    }

    /// <summary>
    /// Ставит чанк в очередь записи. Блоки сжимаются сразу, в вызывающем потоке: потом чанк может измениться.
    /// </summary>
    public void SaveChunk(Chunk chunk)
    {
        ThrowIfFaulted();
        var data = Encode(chunk.Blocks);
        _pendingChunks[chunk.Position] = data;
        _queue.Add(new ChunkWrite(chunk.Position, data));
    }

    /// <summary>
    /// Читает чанк из сохранения (сначала из очереди записи, потом с диска). false — чанка там нет
    /// или его данные испорчены (это пишется в лог: такой чанк сгенерируется заново).
    /// </summary>
    public bool TryLoadChunk(ChunkPos position, out Chunk chunk)
    {
        chunk = null!;
        if (!_pendingChunks.TryGetValue(position, out var data))
        {
            using var command = _readConnections.Value!.CreateCommand();
            command.CommandText = "SELECT data FROM chunks WHERE x = $x AND y = $y AND z = $z";
            command.Parameters.AddWithValue("$x", position.X);
            command.Parameters.AddWithValue("$y", position.Y);
            command.Parameters.AddWithValue("$z", position.Z);
            data = command.ExecuteScalar() as byte[];
            if (data == null)
                return false;
        }

        try
        {
            chunk = Decode(position, data);
            return true;
        }
        catch (InvalidDataException e)
        {
            Console.WriteLine($"[Server] Чанк {position} в сохранении испорчен, он будет создан заново: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Ставит игрока в очередь записи. Инвентарь кодируется сразу, в вызывающем потоке: потом он изменится.
    /// Предмет на курсоре записывается как есть; при загрузке его возвращает в инвентарь сервер.
    /// </summary>
    public void SavePlayer(string name, SavedPlayer player)
    {
        ThrowIfFaulted();
        var write = new PlayerWrite(name, player.Position, player.Look, player.Mode, player.SelectedSlot,
            InventoryCodec.Encode(player.Slots.AsSpan(), ItemIds));
        _pendingPlayers[name] = write;
        _queue.Add(write);
    }

    /// <summary>
    /// Последнее сохранённое состояние игрока или null, если он в этом мире ещё не был. Позицию проверяет вызывающий;
    /// испорченный инвентарь пишется в лог и становится пустым, неверные взгляд, режим и слот — значениями по умолчанию.
    /// </summary>
    public SavedPlayer? LoadPlayer(string name)
    {
        if (_pendingPlayers.TryGetValue(name, out var pending))
            return ToSavedPlayer(pending);

        using var command = _readConnections.Value!.CreateCommand();
        command.CommandText = "SELECT x, y, z, yaw, pitch, mode, selected_slot, inventory FROM players WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var position = new Vector3d(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2));
        var look = new LookAngles(reader.GetFloat(3), reader.GetFloat(4));
        GameMode? mode = null;
        if (!reader.IsDBNull(5))
        {
            if (GameModes.TryParse(reader.GetString(5), out var parsed))
                mode = parsed;
            else
                Console.WriteLine($"[Server] У игрока {name} в сохранении неизвестный режим '{reader.GetString(5)}', будет режим мира");
        }
        var slot = reader.GetInt64(6) is var s and >= 0 and < Inventory.HotbarSize ? (int)s : 0;
        var inventory = reader.IsDBNull(7) ? null : (byte[])reader.GetValue(7);
        return ToSavedPlayer(new PlayerWrite(name, position, look, mode, slot, inventory));
    }

    private SavedPlayer ToSavedPlayer(PlayerWrite player)
    {
        var slots = new ItemStack[Inventory.SlotCount];
        if (player.Inventory != null)
        {
            try
            {
                slots = InventoryCodec.Decode(player.Inventory, ItemIds, _items);
            }
            catch (InvalidDataException e)
            {
                Console.WriteLine($"[Server] Инвентарь игрока {player.Name} в сохранении испорчен, он будет пустым: {e.Message}");
            }
        }

        var look = player.Look.IsFinite ? player.Look.Normalized() : default;
        return new SavedPlayer(player.Position, look, player.Mode, player.SelectedSlot, ImmutableCollectionsMarshal.AsImmutableArray(slots));
    }

    /// <summary>Ждёт, пока всё поставленное в очередь до этого вызова окажется на диске.</summary>
    public void Flush()
    {
        if (_writer == null)
            throw new InvalidOperationException("Поток записи не запущен");

        using var done = new ManualResetEventSlim();
        _queue.Add(new Barrier(done));
        while (!done.Wait(100))
            ThrowIfFaulted();
    }

    /// <summary>Если поток записи упал, бросает его исключение: сервер не должен играть дальше, ничего не сохраняя.</summary>
    public void ThrowIfFaulted()
    {
        if (_fault is { } fault)
            throw new InvalidOperationException("Ошибка записи сохранения", fault);
    }

    /// <summary>Дописывает на диск всю очередь и закрывает файл. Перед вызовом остановить всех, кто пишет в очередь.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer?.Join();
        if (_fault != null)
            Console.Error.WriteLine($"[Server] Сохранение не записано до конца: {_fault}");

        foreach (var connection in _readConnections.Values)
            connection.Dispose();
        _readConnections.Dispose();
        _writeConnection.Dispose();
        _queue.Dispose();
    }

    private void WriteLoop()
    {
        try
        {
            using var writeChunk = _writeConnection.CreateCommand();
            writeChunk.CommandText = "INSERT OR REPLACE INTO chunks (x, y, z, data) VALUES ($x, $y, $z, $data)";
            var cx = writeChunk.Parameters.Add("$x", SqliteType.Integer);
            var cy = writeChunk.Parameters.Add("$y", SqliteType.Integer);
            var cz = writeChunk.Parameters.Add("$z", SqliteType.Integer);
            var cdata = writeChunk.Parameters.Add("$data", SqliteType.Blob);

            using var writePlayer = _writeConnection.CreateCommand();
            writePlayer.CommandText = """
                INSERT OR REPLACE INTO players (name, x, y, z, yaw, pitch, mode, selected_slot, inventory)
                VALUES ($name, $x, $y, $z, $yaw, $pitch, $mode, $slot, $inventory)
                """;
            var pname = writePlayer.Parameters.Add("$name", SqliteType.Text);
            var px = writePlayer.Parameters.Add("$x", SqliteType.Real);
            var py = writePlayer.Parameters.Add("$y", SqliteType.Real);
            var pz = writePlayer.Parameters.Add("$z", SqliteType.Real);
            var pyaw = writePlayer.Parameters.Add("$yaw", SqliteType.Real);
            var ppitch = writePlayer.Parameters.Add("$pitch", SqliteType.Real);
            var pmode = writePlayer.Parameters.Add("$mode", SqliteType.Text);
            var pslot = writePlayer.Parameters.Add("$slot", SqliteType.Integer);
            var pinventory = writePlayer.Parameters.Add("$inventory", SqliteType.Blob);

            var batch = new List<WriteItem>(MaxBatch);
            // Цикл завершается, когда Dispose закрывает очередь и всё поставленное записано.
            foreach (var first in _queue.GetConsumingEnumerable())
            {
                batch.Add(first);
                while (batch.Count < MaxBatch && _queue.TryTake(out var next))
                    batch.Add(next);

                using (var transaction = _writeConnection.BeginTransaction())
                {
                    writeChunk.Transaction = transaction;
                    writePlayer.Transaction = transaction;
                    foreach (var item in batch)
                    {
                        switch (item)
                        {
                            case ChunkWrite chunk:
                                (cx.Value, cy.Value, cz.Value, cdata.Value) = (chunk.Position.X, chunk.Position.Y, chunk.Position.Z, chunk.Data);
                                writeChunk.ExecuteNonQuery();
                                break;
                            case PlayerWrite player:
                                (pname.Value, px.Value, py.Value, pz.Value) = (player.Name, player.Position.X, player.Position.Y, player.Position.Z);
                                (pyaw.Value, ppitch.Value) = (player.Look.Yaw, player.Look.Pitch);
                                pmode.Value = player.Mode?.ToName() ?? (object)DBNull.Value;
                                pslot.Value = player.SelectedSlot;
                                pinventory.Value = player.Inventory ?? (object)DBNull.Value;
                                writePlayer.ExecuteNonQuery();
                                break;
                        }
                    }
                    transaction.Commit();
                }

                // Только после записи на диск: до этого загрузка должна находить данные в памяти.
                // Удаляем, только если значение то же самое: более новую версию ещё предстоит записать.
                foreach (var item in batch)
                {
                    switch (item)
                    {
                        case ChunkWrite chunk:
                            _pendingChunks.TryRemove(new KeyValuePair<ChunkPos, byte[]>(chunk.Position, chunk.Data));
                            break;
                        case PlayerWrite player:
                            _pendingPlayers.TryRemove(new KeyValuePair<string, PlayerWrite>(player.Name, player));
                            break;
                        case Barrier barrier:
                            barrier.Done.Set();
                            break;
                    }
                }
                batch.Clear();
            }
        }
        catch (Exception e)
        {
            // Данные остаются в _pending*, игра может их читать, но на диск они уже не попадут.
            _fault = e;
            Console.Error.WriteLine($"[Server] Ошибка записи сохранения: {e}");
        }
    }

    private byte[] Encode(ReadOnlySpan<BlockId> blocks)
    {
        if (BlockIds.IsIdentity)
            return ChunkCodec.Encode(blocks);

        var buffer = ArrayPool<BlockId>.Shared.Rent(ChunkMath.Volume);
        try
        {
            var span = buffer.AsSpan(0, ChunkMath.Volume);
            BlockIds.ToSave(blocks, span);
            return ChunkCodec.Encode(span);
        }
        finally
        {
            ArrayPool<BlockId>.Shared.Return(buffer);
        }
    }

    private Chunk Decode(ChunkPos position, byte[] data)
    {
        var buffer = ArrayPool<BlockId>.Shared.Rent(ChunkMath.Volume);
        try
        {
            var span = buffer.AsSpan(0, ChunkMath.Volume);
            ChunkCodec.Decode(data, span);
            // Перевод нужен и при совпадающих номерах: он же проверяет, что номера с диска существуют.
            BlockIds.ToRuntime(span);
            return Chunk.FromBlocks(position, span);
        }
        finally
        {
            ArrayPool<BlockId>.Shared.Return(buffer);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // NORMAL в режиме WAL: файл не портится при сбое, но последние транзакции перед сбоем питания могут пропасть.
        // FULL ждал бы диск на каждой транзакции.
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar();
    }

    private WorldInfo ReadInfo()
    {
        object Required(string key) =>
            ReadMeta(_writeConnection, key) ?? throw new InvalidDataException($"В сохранении нет значения '{key}'.");

        var spawn = new Vector3d(
            Convert.ToDouble(Required("spawn_x")), Convert.ToDouble(Required("spawn_y")), Convert.ToDouble(Required("spawn_z")));
        if (!spawn.IsFinite)
            throw new InvalidDataException("В сохранении неверная точка появления.");

        // В мирах версии 1 режима нет: тогда — выживание, как до появления режимов.
        var mode = GameMode.Survival;
        if (ReadMeta(_writeConnection, "default_mode") is { } storedMode && !GameModes.TryParse(storedMode as string, out mode))
            throw new InvalidDataException($"В сохранении неизвестный режим по умолчанию '{storedMode}'.");
        return new WorldInfo(Convert.ToInt32(Required("seed")), spawn, mode);
    }

    /// <summary>
    /// Обновляет схему версии 1 до 2: добавляет игроку столбцы взгляда, режима, слота и инвентаря (таблицу
    /// <c>item_names</c> уже создал конструктор). Одной транзакцией: сбой посередине не оставит полуобновлённый файл.
    /// </summary>
    private void UpgradeFrom1()
    {
        using var transaction = _writeConnection.BeginTransaction();
        foreach (var column in PlayerColumnsAddedIn2)
            Execute(_writeConnection, $"ALTER TABLE players ADD COLUMN {column};");
        Execute(_writeConnection, "UPDATE meta SET value = 2 WHERE key = 'format_version';");
        transaction.Commit();
    }

    /// <param name="table">Таблица имён: <c>block_names</c> или <c>item_names</c>.</param>
    private List<string> ReadNames(string table)
    {
        using var command = _writeConnection.CreateCommand();
        command.CommandText = $"SELECT id, name FROM {table} ORDER BY id";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            // Номера идут подряд с нуля: номер — место имени в списке.
            if (reader.GetInt64(0) != names.Count)
                throw new InvalidDataException($"В таблице {table} сохранения пропущен номер {names.Count}.");
            names.Add(reader.GetString(1));
        }
        return names;
    }

    private void AppendNames(string table, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return;

        using var transaction = _writeConnection.BeginTransaction();
        using var command = _writeConnection.CreateCommand();
        command.CommandText = $"INSERT INTO {table} (id, name) VALUES ((SELECT COUNT(*) FROM {table}), $name)";
        var name = command.Parameters.Add("$name", SqliteType.Text);
        foreach (var newName in names)
        {
            name.Value = newName;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private abstract record WriteItem;
    private sealed record ChunkWrite(ChunkPos Position, byte[] Data) : WriteItem;

    /// <param name="Mode">null — не записан (игрок из версии 1).</param>
    /// <param name="Inventory">Инвентарь в формате <see cref="InventoryCodec"/>; null — не записан (игрок из версии 1).</param>
    private sealed record PlayerWrite(string Name, Vector3d Position, LookAngles Look, GameMode? Mode, int SelectedSlot,
        byte[]? Inventory) : WriteItem;
    private sealed record Barrier(ManualResetEventSlim Done) : WriteItem;
}
