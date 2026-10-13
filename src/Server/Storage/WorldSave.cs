using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Shared;
using Shared.Blocks;
using Shared.Voxels;

namespace Server.Storage;

/// <summary>Неизменные свойства мира, которые записываются при его создании.</summary>
internal sealed record WorldInfo(int Seed, Vector3d SpawnPosition);

/// <summary>
/// Сохранение мира: один файл SQLite. Таблицы <c>meta</c> (версия формата, сид, точка появления),
/// <c>block_names</c> (номер блока в сохранении → имя, см. <see cref="BlockIdMap"/>), <c>chunks</c>
/// (позиция → сжатые блоки в формате <see cref="ChunkCodec"/>) и <c>players</c> (имя → позиция).
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
    /// <summary>Версия схемы базы. Формат блоков чанка версионируется отдельно, первым байтом данных.</summary>
    public const int FormatVersion = 1;

    /// <summary>Сколько записей поток записи кладёт в одну транзакцию.</summary>
    private const int MaxBatch = 512;

    private readonly string _connectionString;
    private readonly SqliteConnection _writeConnection;
    private readonly ThreadLocal<SqliteConnection> _readConnections;
    private readonly BlockingCollection<WriteItem> _queue = new(new ConcurrentQueue<WriteItem>());
    private readonly ConcurrentDictionary<ChunkPos, byte[]> _pendingChunks = new();
    private readonly ConcurrentDictionary<string, Vector3d> _pendingPlayers = new(StringComparer.Ordinal);
    private Thread? _writer;
    private volatile Exception? _fault;

    /// <summary>Путь к файлу базы.</summary>
    public string Path { get; }

    /// <summary>Сид и точка появления. null — мир новый, их ещё нужно записать (<see cref="Initialize"/>).</summary>
    public WorldInfo? Info { get; private set; }

    public BlockIdMap BlockIds { get; }

    private WorldSave(string path, BlockRegistry registry)
    {
        Path = path;
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
            Execute(_writeConnection, """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value);
                CREATE TABLE IF NOT EXISTS block_names (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
                CREATE TABLE IF NOT EXISTS chunks (
                    x INTEGER NOT NULL, y INTEGER NOT NULL, z INTEGER NOT NULL, data BLOB NOT NULL,
                    PRIMARY KEY (x, y, z)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS players (name TEXT PRIMARY KEY, x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL);
                """);

            var version = ReadMeta(_writeConnection, "format_version");
            if (version != null && Convert.ToInt64(version) > FormatVersion)
                throw new InvalidDataException($"Мир сохранён более новой версией игры (формат {version}, поддерживается до {FormatVersion}).");
            if (version != null)
                Info = ReadInfo();

            BlockIds = new BlockIdMap(ReadBlockNames(), registry);
            AppendBlockNames(BlockIds.NewNames);
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
    public static WorldSave Open(string path, BlockRegistry registry, bool startWriter = true)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);

        var save = new WorldSave(path, registry);
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

    /// <summary>Ставит позицию игрока в очередь записи.</summary>
    public void SavePlayer(string name, Vector3d position)
    {
        ThrowIfFaulted();
        _pendingPlayers[name] = position;
        _queue.Add(new PlayerWrite(name, position));
    }

    /// <summary>Последняя сохранённая позиция игрока или null, если он в этом мире ещё не был.</summary>
    public Vector3d? LoadPlayer(string name)
    {
        if (_pendingPlayers.TryGetValue(name, out var pending))
            return pending;

        using var command = _readConnections.Value!.CreateCommand();
        command.CommandText = "SELECT x, y, z FROM players WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var position = new Vector3d(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2));
        return position.IsFinite ? position : null;
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
            writePlayer.CommandText = "INSERT OR REPLACE INTO players (name, x, y, z) VALUES ($name, $x, $y, $z)";
            var pname = writePlayer.Parameters.Add("$name", SqliteType.Text);
            var px = writePlayer.Parameters.Add("$x", SqliteType.Real);
            var py = writePlayer.Parameters.Add("$y", SqliteType.Real);
            var pz = writePlayer.Parameters.Add("$z", SqliteType.Real);

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
                            _pendingPlayers.TryRemove(new KeyValuePair<string, Vector3d>(player.Name, player.Position));
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
        return new WorldInfo(Convert.ToInt32(Required("seed")), spawn);
    }

    private List<string> ReadBlockNames()
    {
        using var command = _writeConnection.CreateCommand();
        command.CommandText = "SELECT id, name FROM block_names ORDER BY id";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            // Номера идут подряд с нуля: номер блока — его место в списке.
            if (reader.GetInt64(0) != names.Count)
                throw new InvalidDataException($"В таблице блоков сохранения пропущен номер {names.Count}.");
            names.Add(reader.GetString(1));
        }
        return names;
    }

    private void AppendBlockNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return;

        using var transaction = _writeConnection.BeginTransaction();
        using var command = _writeConnection.CreateCommand();
        command.CommandText = "INSERT INTO block_names (id, name) VALUES ((SELECT COUNT(*) FROM block_names), $name)";
        var name = command.Parameters.Add("$name", SqliteType.Text);
        foreach (var blockName in names)
        {
            name.Value = blockName;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private abstract record WriteItem;
    private sealed record ChunkWrite(ChunkPos Position, byte[] Data) : WriteItem;
    private sealed record PlayerWrite(string Name, Vector3d Position) : WriteItem;
    private sealed record Barrier(ManualResetEventSlim Done) : WriteItem;
}
