using Microsoft.Data.Sqlite;
using Server.Storage;
using Shared;
using Shared.Items;
using Shared.Quests;
using Shared.Voxels;

namespace Server.Tests.Storage;

public sealed class EntityStorageTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Shared.Blocks.BlockRegistry _blocks = BlockIdMapTests.Registry("base:stone", "base:dirt");
    private ItemRegistry Items => WorldSaveTests.ItemsFor(_blocks);
    private WorldSave Open(bool writer = true) => WorldSave.Open(_directory.SaveFile, _blocks, Items, writer);
    private SavedItemEntity Item(int count = 7) => new(new Vector3d(-0.5, 81, 2.5), new Vector3d(1, 2, -3),
        new ItemStack(Items.Get("base:stone").Id, count), 123, 17);
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void CodecRoundTrip_KeepsMotionStackAndTimersByName()
    {
        var data = EntityCodec.Encode([Item()], Items);
        var reordered = WorldSaveTests.ItemsFor(BlockIdMapTests.Registry("base:dirt", "base:stone"));
        var decoded = Assert.Single(EntityCodec.Decode(data, reordered));
        Assert.Equal(Item() with { Stack = new ItemStack(reordered.Get("base:stone").Id, 7) }, decoded);
        Assert.Equal(EntityCodec.Version, data[0]);
    }

    [Fact]
    public void CodecRejectsTruncationTrailingDataAndNonfinitePosition()
    {
        var data = EntityCodec.Encode([Item()], Items);
        Assert.Throws<InvalidDataException>(() => EntityCodec.Decode(data[..^1], Items));
        Assert.Throws<InvalidDataException>(() => EntityCodec.Decode([.. data, 7], Items));
        Assert.Throws<InvalidDataException>(() => EntityCodec.Encode([Item() with { Position = new(double.NaN, 0, 0) }], Items));
    }

    [Fact]
    public void RemovedModItem_IsSkippedWithoutLosingVanillaItem()
    {
        var registry = new ItemRegistry();
        var mod = registry.Register("mod:gem", 64);
        var stone = registry.Register("base:stone", 64);
        registry.Freeze();
        var data = EntityCodec.Encode([Item() with { Stack = new(mod.Id, 3) },
            Item() with { Stack = new(stone.Id, 11) }], registry);
        Assert.Equal(Item(11), Assert.Single(EntityCodec.Decode(data, Items)));
    }

    [Fact]
    public void CodecRejectsUnknownKindAndNegativeTimers()
    {
        var data = EntityCodec.Encode([Item()], Items);
        data[6] = (byte)'x'; // имя вида начинается после версии, числа записей и длины строки
        Assert.Throws<InvalidDataException>(() => EntityCodec.Decode(data, Items));
        Assert.Throws<InvalidDataException>(() => EntityCodec.Encode([Item() with { AgeTicks = -1 }], Items));
        Assert.Throws<InvalidDataException>(() => EntityCodec.Encode([Item() with { PickupDelayTicks = -1 }], Items));
    }

    [Fact]
    public void EntitySnapshot_IsReadableBeforeWriterAndLatestWinsAfterReopen()
    {
        var chunk = new ChunkPos(-1, 2, 0);
        using (var save = Open(false))
        {
            save.SaveEntities(chunk, [Item()]);
            Assert.Equal(Item(), Assert.Single(save.LoadEntities(chunk)));
            save.SaveEntities(chunk, [Item(8)]);
            Assert.Equal(Item(8), Assert.Single(save.LoadEntities(chunk)));
            save.StartWriter();
            save.Flush();
        }
        using (var save = Open())
        {
            Assert.Equal(Item(8), Assert.Single(save.LoadEntities(chunk)));
            save.SaveEntities(chunk, []);
        }
        using var reopened = Open();
        Assert.Empty(reopened.LoadEntities(chunk));
    }

    [Fact]
    public void CorruptEntityRecord_IsSkipped()
    {
        var chunk = new ChunkPos(-1, 2, 0);
        using (var save = Open()) save.SaveEntities(chunk, [Item()]);
        Execute("UPDATE entities SET data=x'01FFFF'");
        using var reopened = Open();
        Assert.Empty(reopened.LoadEntities(chunk));
    }

    [Theory]
    [InlineData(13, false)]
    [InlineData(0, true)]
    public void PlayerHealthAndDeath_SurviveReopen(int health, bool dead)
    {
        using (var save = Open())
            save.SavePlayer("Krist", new SavedPlayer(new(1, 80, 2), default, GameMode.Survival, 0,
                [.. new ItemStack[Inventory.SlotCount]], new QuestBook(), health, dead));
        using var reopened = Open();
        var player = Assert.IsType<SavedPlayer>(reopened.LoadPlayer("Krist"));
        Assert.Equal(health, player.Health);
        Assert.Equal(dead, player.IsDead);
    }

    [Fact]
    public void Version3_UpgradesAndDefaultsHealth()
    {
        using (var save = Open())
        {
            save.Initialize(new WorldInfo(42, new(0.5, 80, 0.5)));
            save.SavePlayer("Krist", new SavedPlayer(new(1, 80, 2), default, GameMode.Survival, 0,
                [.. new ItemStack[Inventory.SlotCount]], new QuestBook()));
        }
        Execute("ALTER TABLE players DROP COLUMN health; ALTER TABLE players DROP COLUMN is_dead; DROP TABLE entities; UPDATE meta SET value=3 WHERE key='format_version';");
        using var reopened = Open();
        Assert.Equal(3, reopened.UpgradedFrom);
        Assert.Equal(20, reopened.LoadPlayer("Krist")!.Health);
        Assert.False(reopened.LoadPlayer("Krist")!.IsDead);
        Assert.Equal(4, WorldSave.FormatVersion);
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_directory.SaveFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
