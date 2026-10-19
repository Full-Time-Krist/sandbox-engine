using System.Diagnostics;
using Api;
using System.Diagnostics.CodeAnalysis;
using Server.Entities;
using Shared;
using Shared.Items;
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

    /// <summary>
    /// Сколько тиков держать ЛКМ, чтобы рукой наверняка сломать блок у поверхности (дёрн — 18, земля и песок — 15).
    /// Ломание идёт по тикам сервера; тестам, которым важно не время, а результат, хватает этого запаса.
    /// </summary>
    public const int HoldBreakTicks = 40;

    public static void Tick(GameServer server, int times)
    {
        for (var i = 0; i < times; i++)
            server.Tick();
    }

    /// <summary>Маленькая дальность, чтобы тесты не ждали генерации сотен чанков.</summary>
    public const int ViewDistance = 1;
    public const int VerticalDistance = 1;

    /// <param name="mods">Моды-экземпляры кроме встроенного <c>base</c> (без папки).</param>
    /// <param name="modInfos">Моды из папки (с манифестом, контентом и сборкой).</param>
    /// <param name="modLog">Куда писать ошибки обработчиков модов; null — в консоль.</param>
    public static GameServer Create(int viewDistance = ViewDistance, int verticalDistance = VerticalDistance,
        string? savePath = null, int? seed = Seed, GameMode? defaultMode = null,
        IReadOnlyList<(string Id, IMod Mod)>? mods = null, Action<string>? modLog = null,
        IReadOnlyList<Shared.Modding.ModInfo>? modInfos = null) =>
        new(seed, generatorThreads: 2, viewDistance, verticalDistance, spawnColumn: null, savePath, defaultMode,
            modInfos ?? [], mods ?? [], modLog ?? Console.WriteLine);

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

    /// <summary>Клиент в тесте: соединение с сервером и номер игрока из <see cref="JoinAccepted"/>.</summary>
    public sealed class TestClient(IConnection<ClientMessage, ServerMessage> connection) : IConnection<ClientMessage, ServerMessage>
    {
        public int PlayerId { get; set; }

        public void Send(ClientMessage message) => connection.Send(message);

        public bool TryReceive([MaybeNullWhen(false)] out ServerMessage message) => connection.TryReceive(out message);
    }

    /// <summary>
    /// Переносит игрока сразу, без проверки движения: тестам подгрузки, предметов и сохранений нужно ставить
    /// игрока куда угодно, а не идти туда тиками. Клиент получит <see cref="PlayerTeleport"/>.
    /// </summary>
    public static void Teleport(GameServer server, TestClient client, Vector3d feet) => server.Teleport(client.PlayerId, feet);

    /// <summary>
    /// Подключает игрока и ждёт, пока он получит все чанки вокруг точки появления. Полученные сообщения
    /// попадают в <paramref name="received"/>, если он передан, иначе выбрасываются.
    /// </summary>
    public static TestClient JoinAndLoad(GameServer server, List<ServerMessage>? received = null)
    {
        var (connection, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        var client = new TestClient(connection);
        server.Connect(serverSide);
        client.Send(new JoinRequest("Tester"));

        var messages = received ?? [];
        var chunks = 0;
        TickUntil(server, () =>
        {
            while (client.TryReceive(out var message))
            {
                messages.Add(message);
                if (message is JoinAccepted accepted)
                    client.PlayerId = accepted.PlayerId;
                if (message is ChunkData)
                    chunks++;
            }
            return chunks >= AreaSize();
        }, "игрок получит все чанки вокруг себя");

        return client;
    }

    /// <summary>Сколько штук предмета лежит на земле (с шага 7.3 добыча падает предметом, а не сразу в инвентарь).</summary>
    public static int OnGround(GameServer server, ItemId item)
    {
        var count = 0;
        foreach (var entity in server.Entities.Query<DroppedItem>())
        {
            var stack = server.Entities.Get<DroppedItem>(entity).Stack;
            if (stack.Item == item)
                count += stack.Count;
        }
        return count;
    }

    /// <summary>Стопки предметов, лежащих на земле.</summary>
    public static List<ItemStack> ItemsOnGround(GameServer server)
    {
        var stacks = new List<ItemStack>();
        foreach (var entity in server.Entities.Query<DroppedItem>())
            stacks.Add(server.Entities.Get<DroppedItem>(entity).Stack);
        return stacks;
    }

    /// <summary>Тикает, пока всё лежащее на земле не подберётся: игрок должен стоять рядом с добычей.</summary>
    public static void TickUntilPickedUp(GameServer server) =>
        TickUntil(server, () => ItemsOnGround(server).Count == 0, "добыча подберётся");

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
