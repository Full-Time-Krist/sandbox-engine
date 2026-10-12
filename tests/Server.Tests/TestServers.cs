using System.Diagnostics;
using Shared.Voxels;

namespace Server.Tests;

/// <summary>Помощники для тестов с настоящим сервером: мир генерируется в фоновых потоках, его нужно дождаться.</summary>
internal static class TestServers
{
    public const int Seed = 1;

    /// <summary>Сервер, мир которого уже сгенерирован целиком: тесты не зависят от того, какие чанки успели.</summary>
    public static GameServer CreateLoaded(int seed = Seed)
    {
        var server = new GameServer(seed);
        var clock = Stopwatch.StartNew();
        while (server.World.PendingChunks > 0)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"Мир не сгенерировался за 30 с, осталось {server.World.PendingChunks} чанков");
            server.Tick();
            Thread.Sleep(1);
        }
        server.Tick();
        return server;
    }

    /// <summary>Верхний непустой блок столбца.</summary>
    public static BlockPos TopBlock(World world, int x, int z)
    {
        for (var y = (World.MaxChunk.Y + 1) * ChunkMath.Size - 1; y >= World.MinChunk.Y * ChunkMath.Size; y--)
        {
            var pos = new BlockPos(x, y, z);
            if (!world.Chunks.GetBlock(pos).IsAir)
                return pos;
        }
        throw new InvalidOperationException($"Столбец ({x}, {z}) пустой");
    }
}
