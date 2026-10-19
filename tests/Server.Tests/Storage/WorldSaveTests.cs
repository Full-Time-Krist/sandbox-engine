using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Server.Storage;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Quests;
using Shared.Voxels;

namespace Server.Tests.Storage;

public sealed class WorldSaveTests : IDisposable
{
    private static readonly WorldInfo Info = new(Seed: 42, new Vector3d(0.5, 80, -3.5));

    private readonly TempDirectory _directory = new();
    private readonly BlockRegistry _registry = BlockIdMapTests.Registry("base:stone", "base:dirt", "base:sand");

    public void Dispose() => _directory.Dispose();

    /// <summary>Предметы — по одному на блок, как в игре.</summary>
    internal static ItemRegistry ItemsFor(BlockRegistry blocks)
    {
        var items = new ItemRegistry();
        foreach (var block in blocks.All.Where(block => !block.Id.IsAir))
            items.Register(block.Name, 64, block.Id);
        items.Freeze();
        return items;
    }

    private WorldSave Open(BlockRegistry? registry = null, bool startWriter = true, ItemRegistry? items = null) =>
        WorldSave.Open(_directory.SaveFile, registry ?? _registry, items ?? ItemsFor(registry ?? _registry), startWriter);

    /// <summary>Чанк, где каждый блок зависит от позиции: перепутанный или потерянный блок не пройдёт незамеченным.</summary>
    private static Chunk MixedChunk(ChunkPos position, BlockRegistry registry, params string[] names)
    {
        var blocks = new BlockId[ChunkMath.Volume];
        for (var i = 0; i < blocks.Length; i++)
            blocks[i] = i % 7 == 0 ? BlockId.Air : registry.Get(names[i * 31 % names.Length]).Id;
        return Chunk.FromBlocks(position, blocks);
    }

    private static Chunk Load(WorldSave save, ChunkPos position)
    {
        Assert.True(save.TryLoadChunk(position, out var chunk), $"Чанк {position} не найден");
        Assert.Equal(position, chunk.Position);
        return chunk;
    }

    [Fact]
    public void NewSave_HasNoInfo_UntilInitialized_ThenKeepsIt()
    {
        using (var save = Open())
        {
            Assert.Null(save.Info);
            save.Initialize(Info);
            Assert.Equal(Info, save.Info);
        }

        using var reopened = Open();
        Assert.Equal(Info, reopened.Info);
    }

    [Fact]
    public void Chunks_SurviveReopen()
    {
        var positions = new[] { new ChunkPos(0, 0, 0), new ChunkPos(-1, 3, 7), new ChunkPos(-100000, -2, 5) };
        var chunks = positions.Select(p => MixedChunk(p, _registry, "base:stone", "base:dirt", "base:sand")).ToList();
        using (var save = Open())
        {
            save.Initialize(Info);
            chunks.ForEach(save.SaveChunk);
        }

        using var reopened = Open();
        foreach (var chunk in chunks)
            Assert.True(chunk.Blocks.SequenceEqual(Load(reopened, chunk.Position).Blocks), $"Чанк {chunk.Position} отличается");
        Assert.False(reopened.TryLoadChunk(new ChunkPos(1, 0, 0), out _));
    }

    [Fact]
    public void LaterSave_OfSameChunk_Wins()
    {
        var position = new ChunkPos(2, 0, 2);
        using (var save = Open())
        {
            save.SaveChunk(MixedChunk(position, _registry, "base:stone"));
            save.SaveChunk(MixedChunk(position, _registry, "base:sand"));
        }

        using var reopened = Open();
        Assert.True(MixedChunk(position, _registry, "base:sand").Blocks.SequenceEqual(Load(reopened, position).Blocks));
    }

    [Fact]
    public void QueuedChunk_IsLoadedFromMemory_BeforeItIsWritten()
    {
        // Поток записи не запущен: чанк может найтись только в очереди. Так бывает, когда чанк выгрузили
        // и сразу снова запросили — загрузка с диска вернула бы старую версию или ничего.
        var position = new ChunkPos(5, 0, -5);
        var chunk = MixedChunk(position, _registry, "base:dirt", "base:sand");
        using var save = Open(startWriter: false);

        save.SaveChunk(chunk);

        Assert.True(chunk.Blocks.SequenceEqual(Load(save, position).Blocks));

        // А после записи — уже с диска, и тоже правильный.
        save.StartWriter();
        save.Flush();
        Assert.True(chunk.Blocks.SequenceEqual(Load(save, position).Blocks));
    }

    [Fact]
    public void ReorderedRegistry_LoadsBlocksByName()
    {
        // Мир сохранён одним набором блоков, загружается другим: те же имена, другие номера, плюс новый блок.
        var position = new ChunkPos(0, 1, 0);
        var second = new ChunkPos(1, 1, 0);
        var oldRegistry = BlockIdMapTests.Registry("base:stone", "base:dirt");
        var newRegistry = BlockIdMapTests.Registry("base:glass", "base:dirt", "base:stone");
        using (var save = Open(oldRegistry))
            save.SaveChunk(MixedChunk(position, oldRegistry, "base:stone", "base:dirt"));

        using (var save = Open(newRegistry))
        {
            Assert.False(save.BlockIds.IsIdentity);
            Assert.True(MixedChunk(position, newRegistry, "base:stone", "base:dirt").Blocks.SequenceEqual(Load(save, position).Blocks));
            save.SaveChunk(MixedChunk(second, newRegistry, "base:glass", "base:stone"));
        }

        // Стекло записано в таблицу сохранения; старый набор блоков его не знает и видит воздух.
        using var old = Open(oldRegistry);
        Assert.Equal(["base:glass"], old.BlockIds.UnknownNames);
        var withGlass = Load(old, second);
        var expected = MixedChunk(position, newRegistry, "base:glass", "base:stone").Blocks.ToArray()
            .Select(b => b.IsAir || b == newRegistry.Get("base:glass").Id ? BlockId.Air : oldRegistry.Get("base:stone").Id);
        Assert.True(expected.SequenceEqual(withGlass.Blocks.ToArray()));
    }

    /// <summary>Инвентарь из пар «слот — стопка», остальные слоты пустые.</summary>
    private static ImmutableArray<ItemStack> Slots(params (int Slot, ItemStack Stack)[] stacks)
    {
        var slots = new ItemStack[Inventory.SlotCount];
        foreach (var (slot, stack) in stacks)
            slots[slot] = stack;
        return [.. slots];
    }

    /// <summary>Книга заданий: выполненные и набранное у невыполненных.</summary>
    private static QuestBook Quests(string[]? completed = null, params (string Quest, int Count)[] progress)
    {
        var book = new QuestBook();
        foreach (var name in completed ?? [])
            book.Complete(name);
        foreach (var (name, count) in progress)
            book.SetProgress(name, count);
        return book;
    }

    private static void AssertPlayer(SavedPlayer expected, SavedPlayer? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Quests.Completed.Order(), actual.Quests.Completed.Order());
        Assert.Equal(expected.Quests.Progress.OrderBy(e => e.Key), actual.Quests.Progress.OrderBy(e => e.Key));
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.Look, actual.Look);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.SelectedSlot, actual.SelectedSlot);
        Assert.Equal(expected.Slots.ToArray(), actual.Slots.ToArray());
    }

    private ItemStack Stack(string name, int count, ItemRegistry? items = null) =>
        new((items ?? ItemsFor(_registry)).Get(name).Id, count);

    [Fact]
    public void Player_SurvivesReopen_WithLookModeSlotInventoryAndQuests()
    {
        var player = new SavedPlayer(new Vector3d(1_000_000.25, 70.5, -3.75), new LookAngles(2.5f, -0.75f), GameMode.Creative, 7,
            Slots((0, Stack("base:stone", 64)), (35, Stack("base:sand", 1)), (Inventory.CursorSlot, Stack("base:dirt", 5))),
            // Задание мода, которого может не быть, хранится по имени, как и ванильное.
            Quests(["base:get_wood", "other_mod:quest"], ("base:planks", 3)));
        using (var save = Open())
        {
            Assert.Null(save.LoadPlayer("Player"));
            save.SavePlayer("Player", player with { Position = new Vector3d(1, 2, 3), Slots = Slots(), Quests = Quests() });
            save.SavePlayer("Player", player);
            AssertPlayer(player, save.LoadPlayer("Player")); // из очереди записи
        }

        using var reopened = Open();
        AssertPlayer(player, reopened.LoadPlayer("Player")); // с диска
        Assert.Null(reopened.LoadPlayer("Other"));
    }

    [Fact]
    public void DefaultMode_IsSavedWithWorld()
    {
        using (var save = Open())
            save.Initialize(Info with { DefaultMode = GameMode.Creative });

        using var reopened = Open();
        Assert.Equal(GameMode.Creative, reopened.Info!.DefaultMode);
    }

    /// <summary>
    /// Мир, сохранённый до шага 4.2 (блоки и предметы регистрировались кодом в порядке BaseBlocks), открывается
    /// с реестрами из JSON-контента, где номера идут по алфавиту имён: все блоки и предметы на месте.
    /// </summary>
    [Fact]
    public void SaveFromCodeRegistry_LoadsIntoContentRegistry()
    {
        string[] names = ["base:stone", "base:dirt", "base:grass", "base:sand", "base:log", "base:planks"];
        var oldBlocks = BlockIdMapTests.Registry(names);
        var oldItems = ItemsFor(oldBlocks);
        var content = GameContent.Load();
        Assert.NotEqual(oldBlocks.Get("base:stone").Id, content.Blocks.Get("base:stone").Id);
        var position = new ChunkPos(0, 1, 0);
        using (var save = Open(oldBlocks, items: oldItems))
        {
            save.SaveChunk(MixedChunk(position, oldBlocks, names));
            save.SavePlayer("Player", new SavedPlayer(default, default, GameMode.Survival, 0,
                Slots([.. names.Select((name, slot) => (slot, Stack(name, slot + 1, oldItems)))]), Quests()));
        }

        using var reopened = Open(content.Blocks, items: content.Items);

        Assert.Empty(reopened.BlockIds.UnknownNames);
        Assert.Empty(reopened.ItemIds.UnknownNames);
        Assert.True(MixedChunk(position, content.Blocks, names).Blocks.SequenceEqual(Load(reopened, position).Blocks));
        var slots = reopened.LoadPlayer("Player")!.Slots;
        for (var slot = 0; slot < names.Length; slot++)
            Assert.Equal(Stack(names[slot], slot + 1, content.Items), slots[slot]);
    }

    [Fact]
    public void ModList_EmptyForNewWorld_SurvivesReopen_LastWriteWins()
    {
        using (var save = Open())
        {
            Assert.Empty(save.LoadModList());
            save.SaveModList(new Dictionary<string, string> { ["example_mod"] = "1.0.0", ["other_mod"] = "2.1" });
        }
        using (var save = Open())
        {
            Assert.Equal(new Dictionary<string, string> { ["example_mod"] = "1.0.0", ["other_mod"] = "2.1" }, save.LoadModList());
            save.SaveModList(new Dictionary<string, string>());
        }

        using var reopened = Open();
        Assert.Empty(reopened.LoadModList());
    }

    [Fact]
    public void InventoryItems_AreSavedByName_UnknownItemDisappears()
    {
        // Сохранено одним набором предметов, загружается другим: те же имена с другими номерами, земли больше нет.
        var oldBlocks = BlockIdMapTests.Registry("base:stone", "base:dirt", "base:sand");
        var oldItems = ItemsFor(oldBlocks);
        var newBlocks = BlockIdMapTests.Registry("base:glass", "base:sand", "base:stone");
        var newItems = ItemsFor(newBlocks);
        Assert.NotEqual(oldItems.Get("base:stone").Id, newItems.Get("base:stone").Id);
        using (var save = Open(oldBlocks, items: oldItems))
        {
            save.SavePlayer("Player", new SavedPlayer(default, default, GameMode.Survival, 0, Slots(
                (0, Stack("base:stone", 10, oldItems)), (1, Stack("base:dirt", 20, oldItems)), (2, Stack("base:sand", 30, oldItems))), Quests()));
        }

        using var reopened = Open(newBlocks, items: newItems);

        Assert.Equal(["base:dirt"], reopened.ItemIds.UnknownNames);
        var slots = reopened.LoadPlayer("Player")!.Slots;
        Assert.Equal(Stack("base:stone", 10, newItems), slots[0]);
        Assert.True(slots[1].IsEmpty);
        Assert.Equal(Stack("base:sand", 30, newItems), slots[2]);
    }

    [Fact]
    public void Version1Save_IsUpgraded_KeepsChunksAndPlayerPosition()
    {
        // Файл в точности такой, каким его создавала версия 1 (этап 2): без item_names и без новых столбцов игрока.
        var chunk = MixedChunk(new ChunkPos(1, 2, 3), _registry, "base:stone", "base:dirt", "base:sand");
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE meta (key TEXT PRIMARY KEY, value);
            CREATE TABLE block_names (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
            CREATE TABLE chunks (
                x INTEGER NOT NULL, y INTEGER NOT NULL, z INTEGER NOT NULL, data BLOB NOT NULL,
                PRIMARY KEY (x, y, z)) WITHOUT ROWID;
            CREATE TABLE players (name TEXT PRIMARY KEY, x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL);
            INSERT INTO meta VALUES ('format_version', 1), ('seed', 42), ('spawn_x', 0.5), ('spawn_y', 80.0), ('spawn_z', -3.5);
            INSERT INTO block_names VALUES (0, 'base:air'), (1, 'base:stone'), (2, 'base:dirt'), (3, 'base:sand');
            INSERT INTO players VALUES ('Player', 10.5, 64.0, -20.5);
            """);
        Execute("INSERT INTO chunks VALUES (1, 2, 3, $data)", ("$data", ChunkCodec.Encode(chunk)));

        var position = new Vector3d(10.5, 64, -20.5);
        using (var save = Open())
        {
            Assert.Equal(1, save.UpgradedFrom);
            Assert.Equal(Info, save.Info); // режима в версии 1 нет — выживание
            Assert.True(chunk.Blocks.SequenceEqual(Load(save, chunk.Position).Blocks));
            // Новых данных у игрока нет: взгляд вдаль, режим мира, первый слот, пустой инвентарь.
            AssertPlayer(new SavedPlayer(position, default, null, 0, Slots(), Quests()), save.LoadPlayer("Player"));

            save.SavePlayer("Player", new SavedPlayer(position, new LookAngles(1, 0.5f), GameMode.Creative, 2,
                Slots((4, Stack("base:sand", 9))), Quests()));
        }
        Assert.Equal(4L, Execute("SELECT value FROM meta WHERE key = 'format_version'"));

        using var reopened = Open();
        Assert.Null(reopened.UpgradedFrom);
        AssertPlayer(new SavedPlayer(position, new LookAngles(1, 0.5f), GameMode.Creative, 2, Slots((4, Stack("base:sand", 9))), Quests()),
            reopened.LoadPlayer("Player"));
    }

    [Fact]
    public void Version2Save_IsUpgraded_PlayerKeepsInventory_QuestsStartEmpty()
    {
        // Схема версии 2 (этапы 3–6.4): игрок без столбца заданий.
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE meta (key TEXT PRIMARY KEY, value);
            CREATE TABLE block_names (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
            CREATE TABLE item_names (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
            CREATE TABLE chunks (
                x INTEGER NOT NULL, y INTEGER NOT NULL, z INTEGER NOT NULL, data BLOB NOT NULL,
                PRIMARY KEY (x, y, z)) WITHOUT ROWID;
            CREATE TABLE players (name TEXT PRIMARY KEY, x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL,
                yaw REAL NOT NULL DEFAULT 0, pitch REAL NOT NULL DEFAULT 0, mode TEXT, selected_slot INTEGER NOT NULL DEFAULT 0,
                inventory BLOB);
            INSERT INTO meta VALUES ('format_version', 2), ('seed', 42), ('spawn_x', 0.5), ('spawn_y', 80.0), ('spawn_z', -3.5);
            INSERT INTO block_names VALUES (0, 'base:air'), (1, 'base:stone');
            INSERT INTO item_names VALUES (0, 'base:air'), (1, 'base:stone');
            -- Инвентарь: версия 1, один слот — слот 4, предмет 1 (камень), 7 штук.
            INSERT INTO players VALUES ('Player', 1.5, 64.0, 2.5, 0.5, 0.25, 'creative', 4, x'0101040107');
            """);

        var position = new Vector3d(1.5, 64, 2.5);
        var expected = new SavedPlayer(position, new LookAngles(0.5f, 0.25f), GameMode.Creative, 4, Slots((4, Stack("base:stone", 7))), Quests());
        using (var save = Open())
        {
            Assert.Equal(2, save.UpgradedFrom);
            AssertPlayer(expected, save.LoadPlayer("Player"));
            save.SavePlayer("Player", expected with { Quests = Quests(["base:get_wood"]) });
        }
        Assert.Equal(4L, Execute("SELECT value FROM meta WHERE key = 'format_version'"));

        using var reopened = Open();
        Assert.Null(reopened.UpgradedFrom);
        AssertPlayer(expected with { Quests = Quests(["base:get_wood"]) }, reopened.LoadPlayer("Player"));
    }

    /// <summary>
    /// Сущности чанка: до записи читаются из очереди, после — с диска; пустые (null) стирают строку, и стёртое не
    /// возвращается ни из очереди, ни с диска.
    /// </summary>
    [Fact]
    public void Entities_SavedLoadedAndDeleted_ThroughQueueAndDisk()
    {
        var position = new ChunkPos(-3, 1, 7);
        var data = new byte[] { 1, 0 };
        using (var save = Open(startWriter: false))
        {
            Assert.False(save.TryLoadEntities(position, out _));
            save.SaveEntities(position, data);
            // Поток записи не запущен: данные — только в очереди.
            Assert.True(save.TryLoadEntities(position, out var pending));
            Assert.Equal(data, pending);
            save.StartWriter();
            save.Flush();
        }

        using (var reopened = Open())
        {
            Assert.True(reopened.TryLoadEntities(position, out var stored));
            Assert.Equal(data, stored);
            Assert.False(reopened.TryLoadEntities(position with { X = -2 }, out _));

            reopened.SaveEntities(position, null);
            Assert.False(reopened.TryLoadEntities(position, out _));
        }

        Assert.Equal(0L, Execute("SELECT COUNT(*) FROM entities"));
        using var third = Open();
        Assert.False(third.TryLoadEntities(position, out _));
    }

    [Fact]
    public void Version3Save_IsUpgraded_EntitiesTableAppears_PlayerKept()
    {
        // Схема версии 3 (шаги 6.5–7.3): без таблицы сущностей.
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE meta (key TEXT PRIMARY KEY, value);
            CREATE TABLE block_names (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
            CREATE TABLE item_names (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
            CREATE TABLE chunks (
                x INTEGER NOT NULL, y INTEGER NOT NULL, z INTEGER NOT NULL, data BLOB NOT NULL,
                PRIMARY KEY (x, y, z)) WITHOUT ROWID;
            CREATE TABLE players (name TEXT PRIMARY KEY, x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL,
                yaw REAL NOT NULL DEFAULT 0, pitch REAL NOT NULL DEFAULT 0, mode TEXT, selected_slot INTEGER NOT NULL DEFAULT 0,
                inventory BLOB, quests TEXT);
            INSERT INTO meta VALUES ('format_version', 3), ('seed', 42), ('spawn_x', 0.5), ('spawn_y', 80.0), ('spawn_z', -3.5);
            INSERT INTO block_names VALUES (0, 'base:air'), (1, 'base:stone');
            INSERT INTO item_names VALUES (0, 'base:air'), (1, 'base:stone');
            INSERT INTO players VALUES ('Player', 1.5, 64.0, 2.5, 0.5, 0.25, 'creative', 4, x'0101040107', NULL);
            """);

        var expected = new SavedPlayer(new Vector3d(1.5, 64, 2.5), new LookAngles(0.5f, 0.25f), GameMode.Creative, 4,
            Slots((4, Stack("base:stone", 7))), Quests());
        var position = new ChunkPos(0, 2, 0);
        using (var save = Open())
        {
            Assert.Equal(3, save.UpgradedFrom);
            AssertPlayer(expected, save.LoadPlayer("Player"));
            Assert.False(save.TryLoadEntities(position, out _));
            save.SaveEntities(position, [1, 0]);
        }
        Assert.Equal(4L, Execute("SELECT value FROM meta WHERE key = 'format_version'"));

        using var reopened = Open();
        Assert.Null(reopened.UpgradedFrom);
        Assert.True(reopened.TryLoadEntities(position, out _));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":2,"completed":[],"progress":{}}""")]
    [InlineData("""{"version":1,"completed":["no namespace"],"progress":{}}""")]
    [InlineData("""{"version":1,"completed":[],"progress":{"base:a":-1}}""")]
    [InlineData("""{"version":1,"completed":[],"progress":{"base:a":"many"}}""")]
    [InlineData("""{"version":1,"completed":[42],"progress":{}}""")]
    [InlineData("""{"version":1,"progress":{}}""")]
    public void CorruptQuests_StartEmpty_RestOfPlayerKept(string quests)
    {
        var position = new Vector3d(1, 2, 3);
        using (var save = Open())
            save.SavePlayer("Player", new SavedPlayer(position, default, GameMode.Creative, 3, Slots((0, Stack("base:stone", 5))),
                Quests(["base:get_wood"], ("base:planks", 2))));
        Overwrite("UPDATE players SET quests = $quests", ("$quests", quests));

        using var reopened = Open();

        AssertPlayer(new SavedPlayer(position, default, GameMode.Creative, 3, Slots((0, Stack("base:stone", 5))), Quests()),
            reopened.LoadPlayer("Player"));
    }

    [Fact]
    public void CorruptPlayerData_FallsBackToDefaults_PositionKept()
    {
        var position = new Vector3d(1, 2, 3);
        using (var save = Open())
            save.SavePlayer("Player", new SavedPlayer(position, default, GameMode.Creative, 3, Slots((0, Stack("base:stone", 5))), Quests()));
        Overwrite("UPDATE players SET inventory = x'01FF', mode = 'flying', selected_slot = 99, yaw = 1e30, pitch = 5");

        using var reopened = Open();
        var player = reopened.LoadPlayer("Player");

        Assert.NotNull(player);
        Assert.Equal(position, player.Position);
        Assert.Null(player.Mode);
        Assert.Equal(0, player.SelectedSlot);
        Assert.All(player.Slots, s => Assert.True(s.IsEmpty));
        Assert.InRange(player.Look.Yaw, -MathF.PI, MathF.PI);
        Assert.Equal(MathF.PI / 2, player.Look.Pitch);
    }

    [Fact]
    public void CorruptChunk_IsReportedMissing()
    {
        var position = new ChunkPos(0, 0, 0);
        using (var save = Open())
            save.SaveChunk(MixedChunk(position, _registry, "base:stone"));
        Overwrite("UPDATE chunks SET data = x'01FFFF'");

        using var reopened = Open();
        Assert.False(reopened.TryLoadChunk(position, out _));
    }

    [Fact]
    public void NewerFormat_IsRefused()
    {
        using (var save = Open())
            save.Initialize(Info);
        Overwrite($"UPDATE meta SET value = {WorldSave.FormatVersion + 1} WHERE key = 'format_version'");

        Assert.Throws<InvalidDataException>(() => Open());
    }

    /// <summary>Портит базу напрямую, в обход WorldSave.</summary>
    private void Overwrite(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={_directory.SaveFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        Assert.True(command.ExecuteNonQuery() > 0);
    }

    /// <summary>Выполняет SQL напрямую, в обход WorldSave (файла может ещё не быть). Возвращает первое значение результата.</summary>
    private object? Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={_directory.SaveFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }
}
