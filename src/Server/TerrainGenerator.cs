using Shared.Blocks;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Временный генератор холмов по сумме синусоид. На этапе 2 его заменит генерация шумом.
/// Детерминирован: один и тот же чанк всегда получается одинаковым.
/// </summary>
internal sealed class TerrainGenerator(BaseBlocks blocks)
{
    /// <summary>Ниже этой высоты поверхность песчаная, «пляж».</summary>
    private const int SandLevel = 34;
    private const int DirtDepth = 3;

    /// <summary>Высота поверхности: y верхнего блока плюс один.</summary>
    public static int SurfaceHeight(int x, int z)
    {
        var h = 40.0
            + 10 * Math.Sin(x * 0.045) * Math.Cos(z * 0.04)
            + 5 * Math.Sin((x + z) * 0.11)
            + 2 * Math.Cos(x * 0.23 - z * 0.17);
        return (int)Math.Floor(h);
    }

    public Chunk Generate(ChunkPos position)
    {
        var chunk = new Chunk(position);
        var origin = position.Origin;

        for (var lx = 0; lx < ChunkMath.Size; lx++)
        for (var lz = 0; lz < ChunkMath.Size; lz++)
        {
            var height = SurfaceHeight(origin.X + lx, origin.Z + lz);
            var isBeach = height <= SandLevel;

            for (var ly = 0; ly < ChunkMath.Size; ly++)
            {
                var y = origin.Y + ly;
                if (y >= height)
                    break;

                var depth = height - 1 - y; // 0 — верхний блок
                var block =
                    depth == 0 ? (isBeach ? blocks.Sand : blocks.Grass) :
                    depth <= DirtDepth ? (isBeach ? blocks.Sand : blocks.Dirt) :
                    blocks.Stone;

                chunk.Set(new LocalPos(lx, ly, lz), block);
            }
        }

        return chunk;
    }
}
