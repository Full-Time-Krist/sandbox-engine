using System.Diagnostics;
using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

/// <summary>
/// Помощники для тестов с настоящим сервером. Чанки генерируются в фоновых потоках только вокруг игроков,
/// поэтому тест подключает игрока и ждёт, пока тот получит всю область вокруг себя.
/// </summary>
internal static class TestServers
{
    public const int Seed = 1;

    /// <summary>Маленькая дальность, чтобы тесты не ждали генерации сотен чанков.</summary>
    public const int ViewDistance = 1;
    public const int VerticalDistance = 1;

    public static GameServer Create(int viewDistance = ViewDistance, int verticalDistance = VerticalDistance) =>
        new(Seed, generatorThreads: 2, viewDistance, verticalDistance);

    /// <summary>Сколько чанков получает игрок при дальности по умолчанию для тестов.</summary>
    public static int AreaSize(int viewDistance = ViewDistance, int verticalDistance = VerticalDistance) =>
        new PlayerChunkView(viewDistance, verticalDistance, default).AreaSize;

    /// <summary>Тикает сервер, пока условие не выполнится; падает по таймауту, а не виснет.</summary>
    public static void TickUntil(GameServer server, Func<bool> condition, string what, int timeoutSeconds = 20)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
                throw new TimeoutException($"Не дождались за {timeoutSeconds} с: {what}");
            server.Tick();
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Подключает игрока и ждёт, пока он получит все чанки вокруг точки появления. Полученные сообщения
    /// попадают в <paramref name="received"/>, если он передан, иначе выбрасываются.
    /// </summary>
    public static IConnection<ClientMessage, ServerMessage> JoinAndLoad(GameServer server, List<ServerMessage>? received = null)
    {
        var (client, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        server.Connect(serverSide);
        client.Send(new JoinRequest("Tester"));

        var messages = received ?? [];
        var chunks = 0;
        TickUntil(server, () =>
        {
            while (client.TryReceive(out var message))
            {
                messages.Add(message);
                if (message is ChunkData)
                    chunks++;
            }
            return chunks >= AreaSize();
        }, "игрок получит все чанки вокруг себя");

        return client;
    }

    /// <summary>Верхний непустой блок столбца среди загруженных чанков.</summary>
    public static BlockPos TopBlock(World world, int x, int z)
    {
        for (var y = 400; y >= -200; y--)
        {
            var pos = new BlockPos(x, y, z);
            if (!world.Chunks.GetBlock(pos).IsAir)
                return pos;
        }
        throw new InvalidOperationException($"В столбце ({x}, {z}) нет загруженных блоков");
    }
}
