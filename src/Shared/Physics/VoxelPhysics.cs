using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Physics;

/// <summary>Общее столкновение короба с вокселями для игрока и сущностей.</summary>
public static class VoxelPhysics
{
    /// <summary>
    /// Сдвигает короб на <paramref name="delta"/>, обрезая движение о твёрдые блоки: сначала по Y, затем X и Z.
    /// Возвращает фактическое перемещение или null, если по пути есть незагруженный чанк.
    /// </summary>
    public static Vector3d? Sweep(Aabb box, Vector3d delta, ChunkMap chunks, BlockRegistry blocks)
    {
        // Все блоки, которые тело может задеть за тик. Скорость ограничена (не больше ~4 блоков за тик),
        // поэтому их десятки; проходим по ним трижды, без выделения памяти.
        var (min, max) = BlockRange(box.Stretch(delta));

        for (var cy = min.Y >> ChunkMath.Bits; cy <= max.Y >> ChunkMath.Bits; cy++)
        for (var cx = min.X >> ChunkMath.Bits; cx <= max.X >> ChunkMath.Bits; cx++)
        for (var cz = min.Z >> ChunkMath.Bits; cz <= max.Z >> ChunkMath.Bits; cz++)
        {
            if (!chunks.Contains(new ChunkPos(cx, cy, cz)))
                return null;
        }

        var dy = delta.Y;
        foreach (var obstacle in SolidBlocks(min, max, chunks, blocks))
            dy = box.ClipY(obstacle, dy);
        box = box.Offset(new Vector3d(0, dy, 0));

        var dx = delta.X;
        foreach (var obstacle in SolidBlocks(min, max, chunks, blocks))
            dx = box.ClipX(obstacle, dx);
        box = box.Offset(new Vector3d(dx, 0, 0));

        var dz = delta.Z;
        foreach (var obstacle in SolidBlocks(min, max, chunks, blocks))
            dz = box.ClipZ(obstacle, dz);

        return new Vector3d(dx, dy, dz);
    }

    /// <summary>Блоки, которые задевает короб (с запасом в касание гранью: лишний блок ничего не обрежет).</summary>
    private static (BlockPos Min, BlockPos Max) BlockRange(Aabb box) =>
        (BlockPos.Containing(box.Min), BlockPos.Containing(box.Max));

    private static SolidBlockEnumerable SolidBlocks(BlockPos min, BlockPos max, ChunkMap chunks, BlockRegistry blocks) =>
        new(min, max, chunks, blocks);

    /// <summary>Перебор коробов твёрдых блоков в диапазоне. Структура, а не yield: без выделения памяти каждый тик.</summary>
    private readonly struct SolidBlockEnumerable(BlockPos min, BlockPos max, ChunkMap chunks, BlockRegistry blocks)
    {
        public Enumerator GetEnumerator() => new(min, max, chunks, blocks);

        public struct Enumerator(BlockPos min, BlockPos max, ChunkMap chunks, BlockRegistry blocks)
        {
            // Начинаем «перед» первым блоком: MoveNext сначала сдвигает.
            private int _x = min.X - 1, _y = min.Y, _z = min.Z;

            public Aabb Current { get; private set; }

            public bool MoveNext()
            {
                while (true)
                {
                    if (++_x > max.X)
                    {
                        _x = min.X;
                        if (++_z > max.Z)
                        {
                            _z = min.Z;
                            if (++_y > max.Y)
                                return false;
                        }
                    }

                    var block = new BlockPos(_x, _y, _z);
                    if (blocks.IsSolid(chunks.GetBlock(block)))
                    {
                        Current = Aabb.OfBlock(block);
                        return true;
                    }
                }
            }
        }
    }
}
