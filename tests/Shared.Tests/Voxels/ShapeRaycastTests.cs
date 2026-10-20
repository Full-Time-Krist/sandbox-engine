using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Tests.Voxels;

/// <summary>
/// Луч и неполные блоки (шаг 8.6): в блок не-куб луч попадает, только задев его короб; мимо — идёт к блоку за ним.
/// Факел — в клетке (2, 0, 0), за ним по +X камень (3, 0, 0); плита — в клетке (0, 0, 2).
/// </summary>
public class ShapeRaycastTests
{
    private const float Pixel = 1 / 16f;

    private readonly BlockRegistry _blocks = new();
    private readonly ChunkMap _world = new();
    private readonly BlockPos _torch = new(2, 0, 0);
    private readonly BlockPos _stone = new(3, 0, 0);
    private readonly BlockPos _slab = new(0, 0, 2);
    private readonly BlockPos _step = new(0, 0, 4);

    public ShapeRaycastTests()
    {
        var stone = _blocks.Register("test:stone", isOpaque: true).Id;
        var torch = _blocks.Register("test:torch", isOpaque: false, isSolid: false, shape: BlockShape.Torch).Id;
        var slab = _blocks.Register("test:slab", isOpaque: false, shape: BlockShape.Slab).Id;
        // Ступенька: нижняя половина и над ней западная половина — второй короб в списке ближе к лучу сверху на западе.
        var step = _blocks.Register("test:step", isOpaque: false, shape: new BlockShape(null,
            [ShapeBox.FromPixels(0, 0, 0, 16, 8, 16), ShapeBox.FromPixels(0, 8, 0, 8, 16, 16)])).Id;
        _blocks.Freeze();
        _world.Set(new Chunk(default));
        Assert.True(_world.TrySetBlock(_torch, torch));
        Assert.True(_world.TrySetBlock(_stone, stone));
        Assert.True(_world.TrySetBlock(_slab, slab));
        Assert.True(_world.TrySetBlock(_step, step));
    }

    /// <summary>Форма из нескольких коробов: попадание в ближайший к началу луча, а не в первый по списку.</summary>
    [Fact]
    public void MultiBoxShape_HitsNearestBox()
    {
        Assert.Equal(new BlockHit(_step, new BlockPos(0, 1, 0), 2f), Cast(new Vector3d(0.25, 3, 4.5), -Vector3.UnitY));
        Assert.Equal(new BlockHit(_step, new BlockPos(0, 1, 0), 2.5f), Cast(new Vector3d(0.75, 3, 4.5), -Vector3.UnitY));
    }

    private BlockHit? Cast(Vector3d origin, Vector3 direction) => VoxelRaycast.Cast(_world, _blocks, origin, direction, 10f);

    /// <summary>Луч сквозь клетку факела мимо столбика попадает в камень за ним.</summary>
    [Fact]
    public void PastTorchColumn_HitsBlockBehind()
    {
        var hit = Cast(new Vector3d(0.5, 0.5, 0.2), Vector3.UnitX);

        Assert.Equal(new BlockHit(_stone, new BlockPos(-1, 0, 0), 2.5f), hit);
    }

    /// <summary>В столбик — попадание в факел: грань столбика, а не клетки, и расстояние до неё.</summary>
    [Fact]
    public void IntoTorchColumn_HitsTorchFace()
    {
        var hit = Cast(new Vector3d(0.5, 0.3, 0.5), Vector3.UnitX)!.Value;
        Assert.Equal(_torch, hit.Block);
        Assert.Equal(new BlockPos(-1, 0, 0), hit.Normal);
        Assert.Equal(2 + 7 * Pixel - 0.5f, hit.Distance, precision: 5);

        // Выше столбика (10 пикселей) — снова мимо, к камню.
        Assert.Equal(_stone, Cast(new Vector3d(0.5, 0.7, 0.5), Vector3.UnitX)!.Value.Block);
    }

    /// <summary>Над плитой луч проходит её клетку насквозь; ниже половины — попадает в бок; сверху — в верх на 0,5.</summary>
    [Fact]
    public void Slab_UpperHalfIsEmpty()
    {
        Assert.Null(Cast(new Vector3d(-0.5, 0.75, 2.5), Vector3.UnitX));

        Assert.Equal(new BlockHit(_slab, new BlockPos(-1, 0, 0), 0.5f), Cast(new Vector3d(-0.5, 0.25, 2.5), Vector3.UnitX));
        Assert.Equal(new BlockHit(_slab, new BlockPos(0, 1, 0), 2.5f), Cast(new Vector3d(0.5, 3, 2.5), -Vector3.UnitY));
    }

    /// <summary>
    /// Начало в клетке плиты: над коробом — попадание в его верх с нормалью вверх (туда ставится блок); внутри короба —
    /// «изнутри», без нормали, как у куба.
    /// </summary>
    [Fact]
    public void StartInSlabCell_AboveBox_HitsTop_InsideBox_HitsFromInside()
    {
        var above = Cast(new Vector3d(0.5, 0.75, 2.5), -Vector3.UnitY)!.Value;
        Assert.Equal((_slab, new BlockPos(0, 1, 0)), (above.Block, above.Normal));
        Assert.Equal(0.25f, above.Distance, precision: 5);

        Assert.Equal(new BlockHit(_slab, default, 0f), Cast(new Vector3d(0.5, 0.25, 2.5), Vector3.UnitX));

        // Над плитой, глядя вверх, — мимо её короба, дальше пусто.
        Assert.Null(Cast(new Vector3d(0.5, 0.75, 2.5), Vector3.UnitY));
    }
}
