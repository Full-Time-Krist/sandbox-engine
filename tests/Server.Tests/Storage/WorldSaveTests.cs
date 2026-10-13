using Microsoft.Data.Sqlite;
using Server.Storage;
using Shared;
using Shared.Blocks;
using Shared.Voxels;

namespace Server.Tests.Storage;

public sealed class WorldSaveTests : IDisposable
{
    private static readonly WorldInfo Info = new(Seed: 42, new Vector3d(0.5, 80, -3.5));

    private readonly TempDirectory _directory = new();
    private readonly BlockRegistry _registry = BlockIdMapTests.Registry("base:stone", "base:dirt", "base:sand");

    public void Dispose() => _directory.Dispose();

    private WorldSave Open(BlockRegistry? registry = null, bool startWriter = true) =>
        WorldSave.Open(_directory.SaveFile, registry ?? _registry, startWriter);

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

    [Fact]
    public void Player_SurvivesReopen()
    {
        var position = new Vector3d(1_000_000.25, 70.5, -3.75);
        using (var save = Open())
        {
            Assert.Null(save.LoadPlayer("Player"));
            save.SavePlayer("Player", new Vector3d(1, 2, 3));
            save.SavePlayer("Player", position);
            Assert.Equal(position, save.LoadPlayer("Player"));
        }

        using var reopened = Open();
        Assert.Equal(position, reopened.LoadPlayer("Player"));
        Assert.Null(reopened.LoadPlayer("Other"));
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
    private void Overwrite(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_directory.SaveFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.True(command.ExecuteNonQuery() > 0);
    }
}
