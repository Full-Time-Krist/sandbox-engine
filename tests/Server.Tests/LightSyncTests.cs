using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Lighting;
using Shared.Networking;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

/// <summary>
/// Свет у клиента: приходит с чанком (<see cref="ChunkData"/>), изменения — <see cref="ChunkLightChanged"/>.
/// Главная проверка — клиент, собранный только из сообщений, после каждого тика видит тот же свет, что сервер.
/// </summary>
public sealed class LightSyncTests : IDisposable
{
    private readonly GameServer _server = TestServers.Create();
    private readonly BlockId _stone = BaseBlocks.From(GameContent.Load().Blocks).Stone;

    /// <summary>Чанки клиента, собранные только из сообщений сервера.</summary>
    private readonly Dictionary<ChunkPos, Chunk> _clientChunks = [];

    private TestServers.TestClient _client = null!;
    private int _lightMessages;
    private int _unloads;

    public void Dispose() => _server.Dispose();

    private void Join()
    {
        var (connection, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        _client = new TestServers.TestClient(connection);
        _server.Connect(serverSide);
        _client.Send(new JoinRequest("Tester"));
    }

    /// <summary>Тик сервера: сообщения применяются к чанкам клиента, как это делает игра. Возвращает сообщения тика.</summary>
    private List<ServerMessage> Tick()
    {
        _server.Tick();
        var messages = new List<ServerMessage>();
        while (_client.TryReceive(out var message))
        {
            messages.Add(message);
            switch (message)
            {
                case JoinAccepted accepted:
                    _client.PlayerId = accepted.PlayerId;
                    break;
                case ChunkData data:
                    Assert.True(_clientChunks.TryAdd(data.Position, data.ToChunk()), $"Чанк {data.Position} пришёл повторно");
                    break;
                case ChunkUnload unload:
                    Assert.True(_clientChunks.Remove(unload.Position), $"Выгружен чанк {unload.Position}, которого у клиента нет");
                    _unloads++;
                    break;
                case ChunkLightChanged light:
                    // Свет — только тем, у кого чанк есть.
                    Assert.True(_clientChunks.TryGetValue(light.Position, out var chunk), $"Свет чанка {light.Position}, которого у клиента нет");
                    LightCodec.Decode(light.Light.AsMemory(), chunk.Light);
                    _lightMessages++;
                    break;
            }
        }
        return messages;
    }

    private static byte[] LightOf(Chunk chunk)
    {
        var values = new byte[ChunkMath.Volume];
        chunk.Light.CopyTo(values);
        return values;
    }

    /// <summary>Свет каждого чанка клиента равен свету того же чанка на сервере.</summary>
    private void AssertClientLightMatchesServer(string when)
    {
        foreach (var (position, chunk) in _clientChunks)
        {
            var server = _server.World.Chunks.Get(position);
            Assert.True(server is not null, $"{when}: у клиента чанк {position}, которого нет на сервере");
            var expected = LightOf(server);
            var actual = LightOf(chunk);
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                var index = expected.AsSpan().CommonPrefixLength(actual);
                Assert.Fail($"{when}: свет чанка {position} в {LocalPos.FromIndex(index)} — {actual[index]} у клиента, {expected[index]} на сервере");
            }
        }
    }

    /// <summary>
    /// Тикает по одному тику, пока <paramref name="done"/> (сообщения этого тика) не вернёт true; падает по таймауту.
    /// Не <see cref="TestServers.TickUntil"/>: тот сам тикает сервер между проверками, и сообщения двух тиков склеивались бы.
    /// </summary>
    private void RunUntil(Func<List<ServerMessage>, bool> done, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done(Tick()))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException($"Не дождались за 20 с: {what}");
            Thread.Sleep(1);
        }
    }

    /// <summary>Тикает, сверяя свет после каждого тика, пока генерация не утихнет и чанки не перестанут приходить.</summary>
    private void TickUntilSettled(string what)
    {
        var quiet = 0;
        RunUntil(messages =>
        {
            AssertClientLightMatchesServer(what);
            quiet = _server.World.GeneratingCount == 0 && !messages.Any(m => m is ChunkData) && _clientChunks.Count > 0 ? quiet + 1 : 0;
            return quiet >= 10;
        }, what);
    }

    [Fact]
    public void JoiningClient_GetsServerLight_AfterEveryTick()
    {
        Join();
        var resent = 0;

        RunUntil(messages =>
        {
            AssertClientLightMatchesServer("вход");
            // Свет чанка, отправленного в этом же тике, уже в ChunkData: повторять его незачем.
            var sent = messages.OfType<ChunkData>().Select(m => m.Position).ToHashSet();
            resent += messages.OfType<ChunkLightChanged>().Count(m => sent.Contains(m.Position));
            return _clientChunks.Count == TestServers.AreaSize() && _server.World.GeneratingCount == 0;
        }, "вход");
        TickUntilSettled("после входа");

        Assert.Equal(TestServers.AreaSize(), _clientChunks.Count);
        Assert.Equal(0, resent);
        // Свет не пустой: у поверхности есть и солнце, и тень.
        Assert.Contains(_clientChunks.Values, c => !c.Light.IsUniform && LightOf(c).Any(v => v >> 4 == ChunkLight.Max) && LightOf(c).Any(v => v >> 4 == 0));
    }

    [Fact]
    public void NeighborArriving_SendsNewLightOfSentChunks()
    {
        Join();
        TickUntilSettled("вход");
        // Навес у восточной границы крайнего чанка (x = 2): под ним полутень, а с востока — незагруженный чанк.
        var top = 0;
        for (var x = 72; x <= 95; x++)
        for (var z = 4; z <= 27; z++)
            top = Math.Max(top, TestServers.TopBlock(_server.World, x, z).Y);
        for (var x = 72; x <= 95; x++)
        for (var z = 4; z <= 27; z++)
            Assert.True(_server.World.TrySetBlock(new BlockPos(x, top + 6, z), _stone));
        TickUntilSettled("навес построен");
        var before = _clientChunks.Keys.ToDictionary(p => p, p => LightOf(_server.World.Chunks.Get(p)!));
        var lightMessages = _lightMessages;

        // Второй игрок в трёх чанках к востоку: сервер грузит его чанки, и они — соседи крайних чанков первого;
        // под навес приходит свет с востока. Не полагаемся на порядок генерации при входе: в маленьком мире
        // соседи успевают прийти раньше, чем чанк отправлен.
        var (connection, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        _server.Connect(serverSide);
        connection.Send(new JoinRequest("Second"));
        Tick();
        var secondId = connection.TryReceive(out var first) && first is JoinAccepted accepted ? accepted.PlayerId : throw new InvalidOperationException("Второй игрок не вошёл");
        _server.Teleport(secondId, _server.PlayerPosition(_client.PlayerId) + new Vector3d(3 * ChunkMath.Size, 0, 0));
        TickUntilSettled("сосед пришёл");

        var changed = before.Where(entry => !LightOf(_server.World.Chunks.Get(entry.Key)!).AsSpan().SequenceEqual(entry.Value)).ToList();
        Assert.NotEmpty(changed);
        Assert.True(_lightMessages > lightMessages);
        // Всё изменившееся — на восточном краю: свет пришёл из новых соседей.
        Assert.All(changed, entry => Assert.Equal(2, entry.Key.X - BlockPos.Containing(_server.PlayerPosition(_client.PlayerId)).Chunk.X));
    }

    [Fact]
    public void ClientLight_MatchesServer_ThroughEditsAndTravel()
    {
        Join();
        TickUntilSettled("вход");
        var random = new Random(8);
        var start = _server.PlayerPosition(_client.PlayerId);

        for (var hop = 0; hop < 4; hop++)
        {
            // Перелёт: у клиента уходят старые чанки и приходят новые, сервер выгружает ненужные — свет соседей меняется.
            TestServers.Teleport(_server, _client, start + new Vector3d(48 * (hop + 1), 0, 16 * hop));
            for (var tick = 0; tick < 60; tick++)
            {
                for (var edit = 0; edit < 3; edit++)
                {
                    // Правки у границ чанков: так свет переходит из чанка в чанк.
                    var chunk = _clientChunks.Keys.ElementAt(random.Next(_clientChunks.Count)).Origin;
                    var pos = chunk + new BlockPos(Coordinate(random), Coordinate(random), Coordinate(random));
                    _server.World.TrySetBlock(pos, random.Next(2) == 0 ? _stone : BlockId.Air);
                }
                Tick();
                AssertClientLightMatchesServer($"перелёт {hop}, тик {tick}");
            }
        }

        Assert.True(_unloads > 0, "Клиент ни разу не выгружал чанки: перелёт не удался");
    }

    private static int Coordinate(Random random) => random.Next(3) switch
    {
        0 => random.Next(2) * ChunkMath.Mask,
        1 => random.Next(2) == 0 ? 1 : ChunkMath.Mask - 1,
        _ => random.Next(ChunkMath.Size),
    };

    [Fact]
    public void BrokenBlock_SendsLightOfEveryChangedChunk_InSameTick()
    {
        Join();
        TickUntilSettled("вход");
        // Полый каменный куб в воздухе на стыке восьми чанков: внутри темно. Игрок стоит на крыше в творческом.
        var surfaceChunk = BlockPos.Containing(_server.PlayerPosition(_client.PlayerId)).Chunk.Y;
        var y0 = (surfaceChunk + 2) * ChunkMath.Size;
        for (var x = -4; x <= 3; x++)
        for (var y = y0 - 4; y <= y0 + 3; y++)
        for (var z = -4; z <= 3; z++)
        {
            if (x is -4 or 3 || y == y0 - 4 || y == y0 + 3 || z is -4 or 3)
                Assert.True(_server.World.TrySetBlock(new BlockPos(x, y, z), _stone));
        }
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        var feet = new Vector3d(-0.5, y0 + 4, -0.5);
        TestServers.Teleport(_server, _client, feet);
        TickUntilSettled("куб построен");
        var before = _clientChunks.Keys.ToDictionary(p => p, p => LightOf(_server.World.Chunks.Get(p)!));

        // Пробить крышу: солнце заливает куб — свет меняется во всех восьми чанках.
        var roof = new BlockPos(-1, y0 + 3, -1);
        _client.Send(new UseItem(ItemAction.Primary, feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), -System.Numerics.Vector3.UnitY));
        var messages = Tick();

        Assert.Contains(new BlockChanged(roof, BlockId.Air), messages);
        var changed = before.Where(entry => !LightOf(_server.World.Chunks.Get(entry.Key)!).AsSpan().SequenceEqual(entry.Value))
            .Select(entry => entry.Key).ToHashSet();
        Assert.Equal(8, changed.Count);
        var sent = messages.OfType<ChunkLightChanged>().Select(m => m.Position).ToHashSet();
        Assert.Superset(changed, sent);
        AssertClientLightMatchesServer("крыша пробита");
        Assert.Equal(ChunkLight.Max, _clientChunks[new BlockPos(-1, y0 - 3, -1).Chunk].Light.GetSky(new BlockPos(-1, y0 - 3, -1).Local.Index));
    }
}
