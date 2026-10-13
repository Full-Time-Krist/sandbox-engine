using System.Numerics;
using Shared.Blocks;
using Shared.Physics;
using Shared.Voxels;

namespace Shared.Tests.Physics;

public class PlayerPhysicsTests
{
    private const double Tolerance = 1e-9;

    /// <summary>
    /// Мир для тестов: загруженные пустые чанки вокруг точки <paramref name="origin"/> (±48 блоков).
    /// Координаты в тестах — относительно неё, чтобы те же сценарии проверить у центра, на отрицательных
    /// координатах и в миллионе блоков от центра.
    /// </summary>
    private sealed class TestWorld
    {
        public BlockRegistry Blocks { get; } = new();
        public ChunkMap Chunks { get; } = new();
        public BlockId Stone { get; }

        /// <summary>Проходимый блок: прозрачный и не твёрдый, как высокая трава.</summary>
        public BlockId Plant { get; }

        private readonly BlockPos _origin;

        public TestWorld(BlockPos origin = default)
        {
            _origin = origin;
            Stone = Blocks.Register("test:stone", isOpaque: true).Id;
            Plant = Blocks.Register("test:plant", isOpaque: false, isSolid: false).Id;
            Blocks.Freeze();

            var min = (origin - new BlockPos(48, 48, 48)).Chunk;
            var max = (origin + new BlockPos(48, 48, 48)).Chunk;
            for (var x = min.X; x <= max.X; x++)
            for (var y = min.Y; y <= max.Y; y++)
            for (var z = min.Z; z <= max.Z; z++)
                Chunks.Set(new Chunk(new ChunkPos(x, y, z)));
        }

        /// <summary>Точка относительно начала мира.</summary>
        public Vector3d At(double x, double y, double z) => new(_origin.X + x, _origin.Y + y, _origin.Z + z);

        /// <summary>Заполняет блоками параллелепипед (границы включительно, относительно начала мира).</summary>
        public void Fill(int x0, int y0, int z0, int x1, int y1, int z1, BlockId? block = null)
        {
            for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
            for (var z = z0; z <= z1; z++)
                Assert.True(Chunks.TrySetBlock(_origin + new BlockPos(x, y, z), block ?? Stone));
        }

        /// <summary>Пол: верх блоков на высоте 0.</summary>
        public void Floor() => Fill(-20, -1, -20, 20, -1, 20);

        public PlayerState Step(PlayerState state, PlayerInput input) =>
            PlayerPhysics.Step(state, input, Chunks, Blocks);

        /// <summary>Тики с одним вводом. Возвращает конечное состояние и наибольшую высоту ног за это время.</summary>
        public (PlayerState State, double MaxY) Run(PlayerState state, PlayerInput input, int ticks)
        {
            var maxY = state.Position.Y;
            for (var i = 0; i < ticks; i++)
            {
                state = Step(state, input);
                maxY = Math.Max(maxY, state.Position.Y);
            }
            return (state, maxY);
        }
    }

    private static readonly PlayerInput Idle = new(Vector2.Zero);
    private static readonly PlayerInput WalkPlusX = new(Vector2.UnitX);

    /// <summary>Центр мира, отрицательные координаты, миллион блоков от центра (там у double шаг уже 10⁻¹⁰).</summary>
    public static TheoryData<int, int, int> Origins => new()
    {
        { 0, 0, 0 },
        { -1000, -300, -1000 },
        { 1_000_000, 64, -1_000_000 },
    };

    [Theory]
    [MemberData(nameof(Origins))]
    public void Fall_LandsExactlyOnSurface(int ox, int oy, int oz)
    {
        var world = new TestWorld(new BlockPos(ox, oy, oz));
        world.Floor();

        var (state, _) = world.Run(new PlayerState(world.At(0.5, 10, 0.5)), Idle, 60);

        Assert.Equal(world.At(0.5, 0, 0.5).Y, state.Position.Y, Tolerance);
        Assert.True(state.OnGround);
        Assert.Equal(0f, state.Velocity.Y);
    }

    [Fact]
    public void Standing_DoesNotSinkOrDrift()
    {
        var world = new TestWorld();
        world.Floor();
        var state = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);

        for (var i = 0; i < 200; i++)
        {
            state = world.Step(state, Idle);
            Assert.True(state.OnGround, $"тик {i}: потерял опору");
        }

        Assert.Equal(world.At(0.5, 0, 0.5), state.Position);
    }

    [Theory]
    [MemberData(nameof(Origins))]
    public void Wall_StopsBodyFlush_AndKeepsStopping(int ox, int oy, int oz)
    {
        var world = new TestWorld(new BlockPos(ox, oy, oz));
        world.Floor();
        world.Fill(3, 0, -5, 3, 2, 5);
        var state = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);

        // Долго давим в стену: из-за округления тело могло бы понемногу «просачиваться» сквозь неё.
        var (pressed, _) = world.Run(state, WalkPlusX, 200);

        Assert.Equal(world.At(3 - PlayerPhysics.Width / 2, 0, 0).X, pressed.Position.X, 1e-6);
        Assert.Equal(0f, pressed.Velocity.X);
        Assert.False(PlayerPhysics.Collides(pressed.Position, world.Chunks, world.Blocks));
    }

    [Fact]
    public void Wall_SlidesAlongIt()
    {
        var world = new TestWorld();
        world.Floor();
        world.Fill(3, 0, -10, 3, 2, 20);
        var state = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);

        var (moved, _) = world.Run(state, new PlayerInput(Vector2.Normalize(new Vector2(1, 1))), 40);

        Assert.Equal(3 - PlayerPhysics.Width / 2, moved.Position.X, 1e-6);
        Assert.True(moved.Position.Z > 5, $"вдоль стены не сдвинулся: {moved.Position}");
    }

    [Fact]
    public void Diagonal_IsNotFasterThanStraight()
    {
        var world = new TestWorld();
        world.Floor();
        var start = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);

        var (straight, _) = world.Run(start, WalkPlusX, 20);
        // Ввод длиннее 1 (клиент сложил W и D, не нормализовав) — скорость та же.
        var (diagonal, _) = world.Run(start, new PlayerInput(new Vector2(1, 1)), 20);

        var straightDistance = straight.Position.X - start.Position.X;
        var diagonalDistance = Vector3d.Distance(diagonal.Position, start.Position);
        Assert.Equal(straightDistance, diagonalDistance, 1e-4);
        // За секунду разгон занимает пару тиков: пройдено почти WalkSpeed.
        Assert.InRange(straightDistance, PlayerPhysics.WalkSpeed * 0.85, PlayerPhysics.WalkSpeed);
    }

    [Fact]
    public void Jump_PeaksAboutOneAndAQuarterBlocks()
    {
        var world = new TestWorld();
        world.Floor();
        var state = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);

        var (landed, maxY) = world.Run(state, new PlayerInput(Vector2.Zero, Jump: true), 1);
        (landed, var maxAfter) = world.Run(landed, Idle, 40);
        maxY = Math.Max(maxY, maxAfter);

        Assert.InRange(maxY, 1.15, 1.35);
        Assert.Equal(0, landed.Position.Y, Tolerance);
        Assert.True(landed.OnGround);
    }

    [Fact]
    public void Jump_OnlyFromGround()
    {
        var world = new TestWorld();
        world.Floor();
        // В воздухе, падает: прыжок не должен остановить падение.
        var state = new PlayerState(world.At(0.5, 5, 0.5), new Vector3(0, -5, 0));

        var next = world.Step(state, new PlayerInput(Vector2.Zero, Jump: true));

        Assert.True(next.Velocity.Y < -5);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void Jump_OntoStep(int stepHeight, bool shouldClimb)
    {
        var world = new TestWorld();
        world.Floor();
        // Ступень длиной 16 блоков: за 2 секунды игрок проходит меньше и не спрыгивает с дальнего края.
        world.Fill(2, 0, -5, 18, stepHeight - 1, 5);
        var state = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);

        var (jumping, _) = world.Run(state, new PlayerInput(Vector2.UnitX, Jump: true), 40);
        // Прыжок зажат, игрок всё время подпрыгивает: даём приземлиться, чтобы проверить, где он стоит.
        var (result, _) = world.Run(jumping, Idle, 30);

        Assert.True(result.OnGround);
        if (shouldClimb)
        {
            Assert.Equal(stepHeight, result.Position.Y, Tolerance);
            Assert.True(result.Position.X > 3);
        }
        else
        {
            Assert.Equal(0, result.Position.Y, Tolerance);
            Assert.Equal(2 - PlayerPhysics.Width / 2, result.Position.X, 1e-6);
        }
    }

    [Fact]
    public void Ceiling_StopsJump()
    {
        var world = new TestWorld();
        world.Floor();
        // Потолок снизу на высоте 2: над головой (1,8) остаётся 0,2 блока.
        world.Fill(-5, 2, -5, 5, 2, 5);
        var state = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);

        var (afterJump, maxY) = world.Run(state, new PlayerInput(Vector2.Zero, Jump: true), 1);
        (afterJump, var maxAfter) = world.Run(afterJump, Idle, 30);
        maxY = Math.Max(maxY, maxAfter);

        Assert.Equal(2 - PlayerPhysics.Height, maxY, Tolerance);
        Assert.Equal(0, afterJump.Position.Y, Tolerance);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void Doorway(int openingHeight, bool shouldPass)
    {
        var world = new TestWorld();
        world.Floor();
        // Стена x = 3 с проёмом шириной 1 (z = 0) заданной высоты.
        world.Fill(3, 0, -5, 3, 3, -1);
        world.Fill(3, 0, 1, 3, 3, 5);
        world.Fill(3, openingHeight, 0, 3, 3, 0);
        var state = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);

        var (result, _) = world.Run(state, WalkPlusX, 60);

        if (shouldPass)
            Assert.True(result.Position.X > 5, $"не прошёл: {result.Position}");
        else
            Assert.Equal(3 - PlayerPhysics.Width / 2, result.Position.X, 1e-6);
    }

    [Fact]
    public void FastFall_DoesNotPassThroughThinFloor()
    {
        var world = new TestWorld();
        world.Fill(-2, -1, -2, 2, -1, 2); // пол в один блок, под ним пусто
        var state = new PlayerState(world.At(0.5, 40, 0.5), new Vector3(0, -PlayerPhysics.TerminalVelocity, 0));

        var (result, _) = world.Run(state, Idle, 40);

        Assert.Equal(0, result.Position.Y, Tolerance);
        Assert.True(result.OnGround);
    }

    [Fact]
    public void FastRun_DoesNotPassThroughThinWall()
    {
        var world = new TestWorld();
        world.Floor();
        world.Fill(2, 0, -5, 2, 2, 5);
        // Скорость после первого тика ~83 блока/с, то есть ~4 блока за тик: без проверки всего пути тело
        // оказалось бы целиком за стеной (от передней грани тела до задней грани стены 2,2 блока)
        // и столкновения в конечной точке не было бы.
        var state = new PlayerState(world.At(0.5, 0, 0.5), new Vector3(200, 0, 0), OnGround: true);

        var (result, _) = world.Run(state, WalkPlusX, 20);

        Assert.Equal(2 - PlayerPhysics.Width / 2, result.Position.X, 1e-6);
    }

    [Fact]
    public void UnloadedChunk_StandsStill()
    {
        var world = new TestWorld();
        // Загружены чанки вниз до y = −64 (чанк −2), ниже — нет. Тело над этой границей падает
        // и на этом тике задело бы незагруженный чанк.
        var state = new PlayerState(new Vector3d(0.5, -63.9, 0.5), new Vector3(1, -10, 0));

        var next = world.Step(state, WalkPlusX);

        Assert.Equal(state.Position, next.Position);
        Assert.Equal(Vector3.Zero, next.Velocity);
    }

    [Fact]
    public void NoChunks_NothingMoves()
    {
        var blocks = new BlockRegistry();
        var state = new PlayerState(new Vector3d(0.5, 100, 0.5), new Vector3(1, -10, 1));

        var next = PlayerPhysics.Step(state, WalkPlusX, new ChunkMap(), blocks);

        Assert.Equal(state.Position, next.Position);
        Assert.Equal(Vector3.Zero, next.Velocity);
    }

    [Fact]
    public void NonSolidBlocks_ArePassable()
    {
        var world = new TestWorld();
        world.Floor();
        world.Fill(3, 0, -5, 3, 2, 5, world.Plant);
        var state = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);

        var (result, _) = world.Run(state, WalkPlusX, 40);

        Assert.True(result.Position.X > 5);
    }

    [Fact]
    public void Flying_NoGravity_JumpAndDownMoveVertically()
    {
        var world = new TestWorld();
        var state = new PlayerState(world.At(0.5, 10, 0.5), Flying: true);

        var (hover, _) = world.Run(state, Idle, 20);
        var (up, _) = world.Run(state, new PlayerInput(Vector2.Zero, Jump: true), 20);
        var (down, _) = world.Run(state, new PlayerInput(Vector2.Zero, Down: true), 20);

        Assert.Equal(state.Position, hover.Position);
        Assert.True(up.Position.Y > state.Position.Y + 5);
        Assert.True(down.Position.Y < state.Position.Y - 5);
    }

    [Fact]
    public void Flying_StillCollides()
    {
        var world = new TestWorld();
        world.Floor();
        var state = new PlayerState(world.At(0.5, 3, 0.5), Flying: true);

        var (result, _) = world.Run(state, new PlayerInput(Vector2.Zero, Down: true, Sprint: true), 40);

        Assert.Equal(0, result.Position.Y, Tolerance);
    }

    [Fact]
    public void StuckInsideBlock_CanWalkOut()
    {
        var world = new TestWorld();
        world.Floor();
        world.Fill(0, 0, 0, 0, 1, 0); // столб прямо в теле
        var state = new PlayerState(world.At(0.5, 0, 0.5), OnGround: true);
        Assert.True(PlayerPhysics.Collides(state.Position, world.Chunks, world.Blocks));

        var (result, _) = world.Run(state, WalkPlusX, 20);

        Assert.False(PlayerPhysics.Collides(result.Position, world.Chunks, world.Blocks));
    }

    [Fact]
    public void Collides_InsideTrue_TouchingFalse()
    {
        var world = new TestWorld();
        world.Fill(0, 0, 0, 0, 0, 0);

        Assert.True(PlayerPhysics.Collides(world.At(0.5, 0.5, 0.5), world.Chunks, world.Blocks));
        Assert.True(PlayerPhysics.Collides(world.At(-0.2, -1, 0.5), world.Chunks, world.Blocks)); // голова в блоке
        Assert.False(PlayerPhysics.Collides(world.At(0.5, 1, 0.5), world.Chunks, world.Blocks)); // стоит сверху
        Assert.False(PlayerPhysics.Collides(world.At(-0.3, 0, 0.5), world.Chunks, world.Blocks)); // вплотную сбоку
        Assert.False(PlayerPhysics.Collides(world.At(0.5, -1.8, 0.5), world.Chunks, world.Blocks)); // головой в дно
    }
}
