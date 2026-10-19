using System.Numerics;
using Microsoft.Data.Sqlite;
using Server.Entities;
using Server.Tests.Storage;
using Shared;
using Shared.Content;
using Shared.Entities;
using Shared.Items;
using Shared.Voxels;
using static Server.Tests.TestServers;

namespace Server.Tests.Entities;

/// <summary>Предметы на земле в сохранении: перезапуск, выгрузка и загрузка чанка, испорченные данные, без дублей.</summary>
public sealed class EntitySaveTests : IDisposable
{
    private static readonly ItemId Dirt = GameContent.Load().Items.Get("base:dirt").Id;
    private static readonly Vector3d Far = new(2000.5, 0, 2000.5);

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private GameServer CreateSaved() => Create(savePath: _directory.SaveFile);

    /// <summary>Игрок в творческом (чтобы не подобрал), предмет рядом с ним лёг на землю.</summary>
    private static (TestClient Client, EntityId Item) PlayerWithItemNearby(GameServer server, int age = 0)
    {
        var client = JoinAndLoad(server);
        server.SetGameMode(client.PlayerId, GameMode.Creative);
        var feet = server.PlayerPosition(client.PlayerId);
        var item = ItemEntities.Spawn(server.Entities, feet + new Vector3d(1.5, 1, 0), Vector3.Zero, new ItemStack(Dirt, 13));
        server.Entities.Get<DroppedItem>(item).Age = age;
        TickUntil(server, () => server.Entities.Get<Body>(item).OnGround, "предмет ляжет");
        return (client, item);
    }

    private static List<(Vector3d Position, DroppedItem Item)> Items(GameServer server)
    {
        var result = new List<(Vector3d, DroppedItem)>();
        foreach (var entity in server.Entities.Query<DroppedItem, Position>())
            result.Add((server.Entities.Get<Position>(entity).Value, server.Entities.Get<DroppedItem>(entity)));
        return result;
    }

    private long EntityRows()
    {
        using var connection = new SqliteConnection($"Data Source={_directory.SaveFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entities";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Item_SurvivesRestart_WithPositionStackAndAge()
    {
        Vector3d lyingAt;
        using (var server = CreateSaved())
        {
            var (_, item) = PlayerWithItemNearby(server, age: 1000);
            lyingAt = server.Entities.Get<Position>(item).Value;
        }
        Assert.Equal(1, EntityRows());

        using var restarted = CreateSaved();
        JoinAndLoad(restarted);
        TickUntil(restarted, () => Items(restarted).Count == 1, "предмет вернётся с чанком");

        var (position, restored) = Assert.Single(Items(restarted));
        Assert.Equal(lyingAt, position);
        Assert.Equal(new ItemStack(Dirt, 13), restored.Stack);
        // Возраст продолжился, а не начался заново: исчезнет через оставшееся время, а не через 5 минут.
        Assert.InRange(restored.Age, 1000, 1000 + 400);
    }

    /// <summary>Игрок ушёл — чанк выгружен, предмет записан и убран из памяти; вернулся — предмет снова лежит на месте.</summary>
    [Fact]
    public void Item_LeavesWithItsChunk_AndComesBack()
    {
        using var server = CreateSaved();
        var (client, item) = PlayerWithItemNearby(server);
        var home = server.PlayerPosition(client.PlayerId);
        var lyingAt = server.Entities.Get<Position>(item).Value;
        var chunk = BlockPos.Containing(lyingAt).Chunk;

        Teleport(server, client, Far + new Vector3d(0, 100, 0));
        TickUntil(server, () => !server.Entities.IsAlive(item), "предмет уйдёт вместе с чанком");
        Assert.Empty(Items(server)); // в памяти — только сущность самого игрока
        Assert.True(server.Save!.TryLoadEntities(chunk, out _));

        Teleport(server, client, home);
        TickUntil(server, () => Items(server).Count == 1, "предмет вернётся с чанком");
        var (position, restored) = Assert.Single(Items(server));
        Assert.Equal(lyingAt, position);
        Assert.Equal(new ItemStack(Dirt, 13), restored.Stack);
    }

    /// <summary>Мир без сохранения забывает сущности выгруженных чанков, как и сами чанки: память не растёт.</summary>
    [Fact]
    public void WithoutSave_UnloadedChunkEntitiesAreForgotten()
    {
        using var server = Create();
        var (client, item) = PlayerWithItemNearby(server);
        var home = server.PlayerPosition(client.PlayerId);

        Teleport(server, client, Far + new Vector3d(0, 100, 0));
        TickUntil(server, () => !server.Entities.IsAlive(item), "предмет уйдёт вместе с чанком");
        Teleport(server, client, home);
        Tick(server, 60);

        Assert.Empty(Items(server));
    }

    /// <summary>Подобранный предмет не возвращается после перезапуска: опустевший чанк стирается из таблицы.</summary>
    [Fact]
    public void PickedUp_RowDeleted_NotBackAfterRestart()
    {
        using (var server = CreateSaved())
            PlayerWithItemNearby(server);
        Assert.Equal(1, EntityRows());

        using (var server = CreateSaved())
        {
            var client = JoinAndLoad(server);
            TickUntil(server, () => Items(server).Count == 1, "предмет вернётся с чанком");
            server.SetGameMode(client.PlayerId, GameMode.Survival);
            Teleport(server, client, Items(server)[0].Position);
            TickUntilPickedUp(server);
            Assert.Equal(13, server.PlayerInventory(client.PlayerId).Count(Dirt));
        }
        Assert.Equal(0, EntityRows());

        using var third = CreateSaved();
        JoinAndLoad(third);
        Tick(third, 40);
        Assert.Empty(Items(third));
    }

    /// <summary>Испорченная строка сущностей не мешает загрузке мира; при следующей записи чанка она стирается.</summary>
    [Fact]
    public void CorruptRow_WorldLoadsAnyway_RowCleanedUp()
    {
        ChunkPos chunk;
        using (var server = CreateSaved())
        {
            var (_, item) = PlayerWithItemNearby(server);
            chunk = BlockPos.Containing(server.Entities.Get<Position>(item).Value).Chunk;
        }
        using (var connection = new SqliteConnection($"Data Source={_directory.SaveFile};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE entities SET data = x'01FF' WHERE x = $x AND y = $y AND z = $z";
            command.Parameters.AddWithValue("$x", chunk.X);
            command.Parameters.AddWithValue("$y", chunk.Y);
            command.Parameters.AddWithValue("$z", chunk.Z);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        using (var server = CreateSaved())
        {
            JoinAndLoad(server);
            Tick(server, 20);
            Assert.Empty(Items(server));
        }
        Assert.Equal(0, EntityRows());
    }

    /// <summary>
    /// Предмет создан в незагруженном месте (так сможет мод) и записан автосохранением. Когда чанк загрузится,
    /// строка не должна вернуть вторую копию: живой предмет — уже в памяти.
    /// </summary>
    [Fact]
    public void ItemInUnloadedChunk_AutosavedThenChunkLoads_NoDuplicate()
    {
        using var server = CreateSaved();
        var client = JoinAndLoad(server);
        server.SetGameMode(client.PlayerId, GameMode.Creative);
        ItemEntities.Spawn(server.Entities, Far + new Vector3d(0, 40, 0), Vector3.Zero, new ItemStack(Dirt, 2));
        Tick(server, GameServer.AutosaveIntervalTicks);
        server.Save!.Flush();
        Assert.Equal(1, EntityRows());

        Teleport(server, client, Far + new Vector3d(0, 42, 0));
        var farChunk = BlockPos.Containing(Far + new Vector3d(0, 40, 0)).Chunk;
        TickUntil(server, () => server.World.Chunks.Contains(farChunk), "сервер загрузит дальний чанк");
        Tick(server, 5);

        Assert.Equal([new ItemStack(Dirt, 2)], ItemsOnGround(server));
    }
}
