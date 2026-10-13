using Shared.Physics;
using Shared.Voxels;

namespace Shared.Tests.Physics;

public class AabbTests
{
    private static readonly Aabb Block = Aabb.OfBlock(new BlockPos(2, 0, 0)); // куб x 2..3, y 0..1, z 0..1

    private static Aabb Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        new(new Vector3d(x0, y0, z0), new Vector3d(x1, y1, z1));

    [Fact]
    public void Intersects_TouchingFacesIsNot()
    {
        Assert.True(Block.Intersects(Box(1.5, 0.2, 0.2, 2.5, 0.8, 0.8)));
        Assert.False(Block.Intersects(Box(1, 0, 0, 2, 1, 1))); // касание гранью
        Assert.False(Block.Intersects(Box(2, 1, 0, 3, 2, 1))); // стоит сверху
    }

    [Fact]
    public void Clip_StopsFlushAtObstacle()
    {
        var body = Box(0.5, 0, 0, 1.5, 1, 1);

        Assert.Equal(0.5, body.ClipX(Block, 3), 12);   // вперёд — до стены
        Assert.Equal(0.25, body.ClipX(Block, 0.25)); // не доходит — без изменений
        Assert.Equal(-3, body.ClipX(Block, -3));     // назад — стена не мешает
    }

    [Fact]
    public void Clip_IgnoresObstacleNotOnPath()
    {
        // Тело выше блока: движение по X его не задевает.
        var body = Box(0.5, 1, 0, 1.5, 2, 1);

        Assert.Equal(3, body.ClipX(Block, 3));
    }

    [Fact]
    public void Clip_EachAxis()
    {
        var below = Box(2, -2, 0, 3, -1, 1);
        var beside = Box(2, 0, -2, 3, 1, -1);

        Assert.Equal(1, below.ClipY(Block, 5), 12);
        Assert.Equal(1, beside.ClipZ(Block, 5), 12);
        Assert.Equal(-1, Box(2, 2, 0, 3, 3, 1).ClipY(Block, -5), 12);
    }

    [Fact]
    public void Clip_BodyRoundedSlightlyIntoWall_IsStillStopped()
    {
        // После сложения double тело может оказаться на 10⁻¹⁰ внутри стены. Стена всё равно должна держать,
        // а не считаться «уже пересечённой» (такие препятствия пропускаются, чтобы застрявший мог выйти).
        var body = Box(1, 0, 0, 2 + 1e-10, 1, 1);

        Assert.True(body.ClipX(Block, 0.5) <= 0);
    }

    [Fact]
    public void Clip_ObstacleAlreadyInside_IsIgnored()
    {
        var body = Box(2.2, 0, 0, 3.2, 1, 1);

        Assert.Equal(0.5, body.ClipX(Block, 0.5));
        Assert.Equal(-0.5, body.ClipX(Block, -0.5));
    }

    [Fact]
    public void Stretch_CoversWholePath()
    {
        var body = Box(0, 0, 0, 1, 2, 1);

        Assert.Equal(Box(-3, 0, 0, 1, 2.5, 1), body.Stretch(new Vector3d(-3, 0.5, 0)));
    }
}
