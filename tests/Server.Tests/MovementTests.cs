using System.Numerics;
using Shared;
using Shared.Blocks;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

/// <summary>Проверка движения на сервере: честный клиент проходит без поправок, нечестный возвращается назад.</summary>
public sealed class MovementTests : IDisposable
{
    private readonly GameServer _server = TestServers.Create();
    private readonly TestServers.TestClient _client;
    private readonly BlockRegistry _registry = new();
    private readonly BaseBlocks _blocks;
    private readonly Vector3d _spawn;

    /// <summary>Номер последнего телепорта, как его знает клиент.</summary>
    private int _teleportId;

    public MovementTests()
    {
        // Реестр с теми же блоками, что у сервера: id совпадают, физика клиента видит тот же мир.
        _blocks = BaseBlocks.Register(_registry);
        _registry.Freeze();

        var messages = new List<ServerMessage>();
        _client = TestServers.JoinAndLoad(_server, messages);
        _spawn = Assert.IsType<JoinAccepted>(messages[0]).StartPosition;
    }

    public void Dispose() => _server.Dispose();

    private Vector3d ServerPosition => _server.PlayerPosition(_client.PlayerId);

    /// <summary>Отправляет перемещения (в один тик сервера) и возвращает телепорты, которые прислал сервер.</summary>
    private List<PlayerTeleport> Send(params Vector3d[] positions)
    {
        foreach (var position in positions)
            _client.Send(new PlayerMove(position, _teleportId));
        _server.Tick();
        return ReceiveTeleports();
    }

    private List<PlayerTeleport> ReceiveTeleports()
    {
        var teleports = new List<PlayerTeleport>();
        while (_client.TryReceive(out var message))
        {
            if (message is PlayerTeleport teleport)
            {
                teleports.Add(teleport);
                _teleportId = teleport.TeleportId;
            }
        }
        return teleports;
    }

    /// <summary>Переносит игрока сервером (как команда) и принимает телепорт, как это сделал бы клиент.</summary>
    private void Teleport(Vector3d feet)
    {
        TestServers.Teleport(_server, _client, feet);
        ReceiveTeleports();
    }

    /// <summary>Игрок висит в воздухе в 10 блоках над точкой появления: путь по горизонтали свободен.</summary>
    private Vector3d InAir => _spawn + new Vector3d(0, 10, 0);

    [Fact]
    public void HonestClient_WalkJumpFlyFall_IsNeverCorrected()
    {
        // Клиент считает движение той же физикой по тем же чанкам и шлёт позицию каждый тик, как настоящий.
        var state = new PlayerState(_spawn);
        var teleports = new List<PlayerTeleport>();

        void Run(PlayerInput input, int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                var next = PlayerPhysics.Step(state, input, _server.World.Chunks, _registry);
                teleports.AddRange(next.Position != state.Position ? Send(next.Position) : Send());
                state = next;
            }
        }

        Run(new PlayerInput(Vector2.UnitX, Jump: true, Sprint: true), 40);  // разбег с прыжками по рельефу
        Run(new PlayerInput(-Vector2.UnitX), 20);
        state = state with { Flying = true };
        Run(new PlayerInput(Vector2.UnitY, Jump: true, Sprint: true), 30); // быстрый полёт вверх и вбок
        Run(new PlayerInput(Vector2.Zero, Sprint: true), 10);
        state = state with { Flying = false };
        Run(new PlayerInput(Vector2.Zero), 60);                             // падение до земли

        Assert.Empty(teleports);
        Assert.True(state.OnGround, "тест должен закончиться падением на землю");
        Assert.Equal(state.Position, ServerPosition);
    }

    [Fact]
    public void IntoGround_IsRejected()
    {
        var teleports = Send(_spawn - new Vector3d(0, 2, 0));

        Assert.Equal([new PlayerTeleport(_spawn, 1)], teleports);
        Assert.Equal(_spawn, ServerPosition);
    }

    [Fact]
    public void ThroughThinWall_IsRejected_ButSameMoveWithoutWallIsAccepted()
    {
        var start = InAir;
        var target = start + new Vector3d(2, 0, 0);

        Teleport(start);
        Assert.Empty(Send(target));

        // Стена толщиной в блок на пути, в рост игрока. Конечная точка за ней свободна: поймать можно
        // только по пути, а не по конечной позиции.
        var wallX = (int)Math.Floor(start.X) + 1;
        var feetY = (int)Math.Floor(start.Y);
        var z = (int)Math.Floor(start.Z);
        for (var y = feetY; y <= feetY + 1; y++)
            Assert.True(_server.World.TrySetBlock(new BlockPos(wallX, y, z), _blocks.Stone));
        Teleport(start);
        Assert.False(PlayerPhysics.Collides(target, _server.World.Chunks, _registry));

        var teleports = Send(target);

        Assert.Single(teleports);
        Assert.Equal(start, ServerPosition);
    }

    [Fact]
    public void FarJump_IsRejected()
    {
        Teleport(InAir);

        var teleports = Send(InAir + new Vector3d(100, 0, 0));

        Assert.Single(teleports);
        Assert.Equal(InAir, ServerPosition);
    }

    [Fact]
    public void ManyMovesInOneTick_RunOutOfBudget()
    {
        // 20 шагов по блоку за один тик сервера: запаса на 10 тиков быстрого полёта (~11,5 блока) не хватит.
        Teleport(InAir);
        var moves = Enumerable.Range(1, 20).Select(i => InAir + new Vector3d(i, 0, 0)).ToArray();

        var teleports = Send(moves);

        // Первый отклонённый шаг возвращает назад; остальные отправлены до телепорта и игнорируются.
        var teleport = Assert.Single(teleports);
        var accepted = teleport.Position.X - InAir.X;
        Assert.InRange(accepted, MovementCheck.BudgetTicks * PlayerPhysics.FlySprintSpeed * GameTime.TickDuration - 1,
            MovementCheck.MaxHorizontalPerTick * MovementCheck.BudgetTicks);
    }

    [Fact]
    public void FastestFlight_KeptUpForLong_IsAccepted()
    {
        // Быстрый полёт 3 секунды, каждый тик ровно предельная скорость: запас пополняется, и длинный полёт
        // в него не упирается. Летаем туда-обратно над точкой появления, чтобы не задеть рельеф.
        Teleport(InAir);
        var perTick = PlayerPhysics.FlySprintSpeed * GameTime.TickDuration;
        for (var x = 0; x <= 6; x++)
            Assert.False(PlayerPhysics.Collides(InAir + new Vector3d(x, 0, 0), _server.World.Chunks, _registry), "путь полёта должен быть свободен");
        var position = InAir;

        for (var i = 0; i < 60; i++)
        {
            var direction = i / 5 % 2 == 0 ? 1 : -1;
            position += new Vector3d(direction * perTick, 0, 0);
            Assert.Empty(Send(position));
        }
    }

    [Fact]
    public void MovesSentBeforeTeleport_AreIgnored()
    {
        Teleport(InAir);
        var staleId = _teleportId;
        Teleport(InAir + new Vector3d(0, 1, 0));

        // Честное по величине перемещение, но посчитанное от позиции до телепорта.
        _client.Send(new PlayerMove(InAir + new Vector3d(0.2, 0, 0), staleId));
        _server.Tick();

        Assert.Empty(ReceiveTeleports());
        Assert.Equal(InAir + new Vector3d(0, 1, 0), ServerPosition);
    }

    [Fact]
    public void StuckInsideBlock_CanMoveOut()
    {
        // Ноги на блок ниже поверхности: тело в земле. Шаг вверх на поверхность выводит из блоков.
        var inside = _spawn - new Vector3d(0, 1, 0);
        Teleport(inside);

        Assert.Empty(Send(_spawn));
        Assert.Equal(_spawn, ServerPosition);
    }
}
