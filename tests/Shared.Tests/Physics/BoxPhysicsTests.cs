using System.Numerics;
using Shared.Blocks;
using Shared.Physics;
using Shared.Voxels;

namespace Shared.Tests.Physics;

/// <summary>Падающее тело (предмет на земле): гравитация, опора, стены, трение, незагруженный мир.</summary>
public class BoxPhysicsTests
{
    private const double Tolerance = 1e-9;

    // Как у предмета на земле на сервере (ItemEntities.Shape).
    private static readonly BodyShape Item = new(Width: 0.25, Height: 0.25, Gravity: 16, TerminalVelocity: 40,
        AirDrag: 0.02f, GroundDrag: 0.4f);

    private readonly BlockRegistry _blocks = new();
    private readonly ChunkMap _chunks = new();
    private readonly BlockId _stone;

    public BoxPhysicsTests()
    {
        _stone = _blocks.Register("test:stone", isOpaque: true).Id;
        _blocks.Freeze();
        // Загружены чанки от −1 до 0 по всем осям: мир от −32 до 31.
        for (var x = -1; x <= 0; x++)
        for (var y = -1; y <= 0; y++)
        for (var z = -1; z <= 0; z++)
            _chunks.Set(new Chunk(new ChunkPos(x, y, z)));
        // Пол: верх блоков на высоте 0.
        Fill(-30, -1, -30, 30, -1, 30);
    }

    private void Fill(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        for (var z = z0; z <= z1; z++)
            Assert.True(_chunks.TrySetBlock(new BlockPos(x, y, z), _stone));
    }

    private BodyState Run(BodyState state, int ticks)
    {
        for (var i = 0; i < ticks; i++)
            state = BoxPhysics.Fall(state, Item, _chunks, _blocks);
        return state;
    }

    [Fact]
    public void Falls_AndLandsExactlyOnFloor()
    {
        var start = new BodyState(new Vector3d(0.5, 10, 0.5));

        var after1 = BoxPhysics.Fall(start, Item, _chunks, _blocks);
        Assert.True(after1.Position.Y < 10);
        Assert.True(after1.Velocity.Y < 0);
        Assert.False(after1.OnGround);

        var landed = Run(start, 100);
        Assert.Equal(0, landed.Position.Y, Tolerance);
        Assert.True(landed.OnGround);
        Assert.Equal(Vector3.Zero, landed.Velocity);
    }

    /// <summary>Лежащий предмет не сползает и не дрожит: положение не меняется тик за тиком.</summary>
    [Fact]
    public void Resting_StaysPut()
    {
        var resting = Run(new BodyState(new Vector3d(3.3, 0.5, -2.7)), 40);

        var later = Run(resting, 200);

        Assert.Equal(resting.Position, later.Position);
        Assert.True(later.OnGround);
    }

    /// <summary>На любой скорости тело не проходит сквозь тонкую стену: путь за тик проверяется целиком.</summary>
    [Fact]
    public void FastBody_DoesNotPassThroughWall()
    {
        Fill(5, 0, -5, 5, 5, 5); // стена толщиной в блок, ближняя грань — x = 5
        var start = new BodyState(new Vector3d(0.5, 1, 0.5), new Vector3(200, 0, 0));

        var after = Run(start, 20);

        Assert.Equal(5 - Item.Width / 2, after.Position.X, Tolerance);
        // На такой скорости стена — уже в первом тике, и удар гасит скорость по оси (а не оставляет «давить» в стену).
        var first = BoxPhysics.Fall(start, Item, _chunks, _blocks);
        Assert.Equal(5 - Item.Width / 2, first.Position.X, Tolerance);
        Assert.Equal(0, first.Velocity.X);
    }

    /// <summary>Трение останавливает брошенный по земле предмет за короткое расстояние, и дальше он лежит.</summary>
    [Fact]
    public void SlidingOnGround_StopsByFriction()
    {
        var start = new BodyState(new Vector3d(0.5, 0, 0.5), new Vector3(6, 0, 0), OnGround: true);

        var after = Run(start, 40);

        Assert.Equal(Vector3.Zero, after.Velocity);
        Assert.InRange(after.Position.X - 0.5, 0.05, 1.0);
        Assert.Equal(after.Position, Run(after, 20).Position);
    }

    /// <summary>Путь в незагруженный чанк: тело стоит на месте без скорости — не проваливается сквозь непришедший мир.</summary>
    [Fact]
    public void UnloadedChunk_BodyStandsStill()
    {
        // Над миром: чанк y = 1 не загружен, а тело летит в него.
        var start = new BodyState(new Vector3d(0.5, 31.9, 0.5), new Vector3(0, 30, 0));

        var after = BoxPhysics.Fall(start, Item, _chunks, _blocks);

        Assert.Equal(start.Position, after.Position);
        Assert.Equal(Vector3.Zero, after.Velocity);
    }

    [Fact]
    public void FallSpeed_LimitedByTerminalVelocity()
    {
        var start = new BodyState(new Vector3d(0.5, 25, 0.5), new Vector3(0, -100, 0));

        var after = BoxPhysics.Fall(start, Item, _chunks, _blocks);

        // Предел, затем сопротивление воздуха за тик.
        Assert.Equal(-Item.TerminalVelocity * (1 - Item.AirDrag), after.Velocity.Y, 1e-4);
        Assert.Equal(25 - Item.TerminalVelocity * GameTime.TickDuration, after.Position.Y, 1e-6);
    }
}
