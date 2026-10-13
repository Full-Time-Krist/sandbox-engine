using System.Numerics;
using Server.Storage;
using Shared;
using Shared.Blocks;
using Shared.Items;
using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests.Storage;

/// <summary>Сохранение глазами сервера: перезапуск, выгрузка чанков, автосохранение.</summary>
public sealed class SaveLoadTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private GameServer Create(int? seed = TestServers.Seed) => TestServers.Create(savePath: _directory.SaveFile, seed: seed);

    /// <summary>Ломает верхний блок столбца (0, 0) так же, как игрок: через сообщения. Возвращает его позицию.</summary>
    private static BlockPos BreakSurface(GameServer server, TestServers.TestClient client)
    {
        var surface = TestServers.TopBlock(server.World, 0, 0);
        var eye = new Vector3d(0.5, surface.Y + 3.5, 0.5);
        TestServers.Teleport(server, client, eye);
        client.Send(new UseItem(ItemAction.Primary, eye, -Vector3.UnitY));
        server.Tick();

        Assert.True(server.World.Chunks.GetBlock(surface).IsAir);
        return surface;
    }

    [Fact]
    public void BrokenBlock_SurvivesRestart_AndSeedComesFromSave()
    {
        BlockPos surface;
        using (var server = Create(seed: 5))
            surface = BreakSurface(server, TestServers.JoinAndLoad(server));

        using var restarted = Create(seed: 99);
        TestServers.JoinAndLoad(restarted);

        Assert.Equal(5, restarted.World.Seed);
        Assert.True(restarted.World.Chunks.GetBlock(surface).IsAir);
    }

    [Fact]
    public void GeneratedChunks_AreSaved_EvenUnchanged()
    {
        // Сохраняется каждый сгенерированный чанк, а не только изменённые: генератор будет меняться,
        // и нетронутые места не должны пересоздаваться по-новому.
        using (var server = Create())
            TestServers.JoinAndLoad(server);

        using var restarted = Create();
        TestServers.JoinAndLoad(restarted);

        Assert.Equal(0, restarted.World.GeneratedCount);
        Assert.True(restarted.World.LoadedCount >= TestServers.AreaSize());
    }

    [Fact]
    public void Player_StartsWhereHeLeft()
    {
        var position = new Vector3d(40.25, 90.5, -7.75);
        using (var server = Create())
        {
            var client = TestServers.JoinAndLoad(server);
            TestServers.Teleport(server, client, position);
            server.Tick();
        }

        using var restarted = Create();
        var messages = new List<ServerMessage>();
        TestServers.JoinAndLoad(restarted, messages);

        Assert.Equal(position, Assert.IsType<JoinAccepted>(messages[0]).StartPosition);
    }

    [Fact]
    public void ChangedChunk_SurvivesUnload_AndComesBackChanged()
    {
        using var server = Create();
        var client = TestServers.JoinAndLoad(server);
        var surface = BreakSurface(server, client);
        var chunk = surface.Chunk;

        // Улетаем так далеко, что сервер выгружает чанк из памяти, и возвращаемся.
        var eye = new Vector3d(0.5, surface.Y + 3.5, 0.5);
        TestServers.Teleport(server, client, eye + new Vector3d(32 * 20, 0, 0));
        TestServers.TickUntil(server, () => !server.World.Chunks.Contains(chunk), "сервер выгрузит чанк");
        TestServers.Teleport(server, client, eye);
        TestServers.TickUntil(server, () => server.World.Chunks.Contains(chunk), "чанк загрузится снова");

        Assert.True(server.World.Chunks.GetBlock(surface).IsAir);
        Assert.True(server.World.LoadedCount > 0);
    }

    [Fact]
    public void Autosave_WritesChangedChunks_WithoutExit()
    {
        using var server = Create();
        var surface = BreakSurface(server, TestServers.JoinAndLoad(server));
        Assert.Equal(1, server.World.DirtyCount);

        for (var i = 0; i < GameServer.AutosaveIntervalTicks; i++)
            server.Tick();
        Assert.Equal(0, server.World.DirtyCount);

        // Игра ещё идёт, но изменение уже на диске: его видит второй читатель того же файла.
        server.Save!.Flush();
        var registry = new BlockRegistry();
        BaseBlocks.Register(registry);
        registry.Freeze();
        using var reader = WorldSave.Open(_directory.SaveFile, registry);
        Assert.True(reader.TryLoadChunk(surface.Chunk, out var saved));
        Assert.True(saved.Get(surface.Local).IsAir);
    }
}
