using Shared.Blocks;
using Shared.Voxels;

namespace Client;

/// <summary>Тестовый чанк для проверки построения меша. Уйдёт на шаге 1.5, когда мир начнёт присылать сервер.</summary>
internal static class DebugTerrain
{
    public static Chunk CreateTestChunk(BaseBlocks blocks)
    {
        var chunk = new Chunk(new ChunkPos(0, 0, 0));

        // Холмы: трава сверху, под ней два слоя земли, ниже камень.
        for (var x = 0; x < ChunkMath.Size; x++)
        for (var z = 0; z < ChunkMath.Size; z++)
        {
            var height = (int)(8 + 3 * MathF.Sin(x * 0.35f) + 3 * MathF.Cos(z * 0.3f));
            for (var y = 0; y < height; y++)
            {
                var block = y == height - 1 ? blocks.Grass : y >= height - 3 ? blocks.Dirt : blocks.Stone;
                chunk.Set(new LocalPos(x, y, z), block);
            }
        }

        // Ряд столбиков из всех блоков, чтобы сравнить цвета.
        BlockId[] palette = [blocks.Stone, blocks.Dirt, blocks.Grass, blocks.Sand, blocks.Log, blocks.Planks];
        for (var i = 0; i < palette.Length; i++)
        {
            for (var y = 14; y < 18; y++)
                chunk.Set(new LocalPos(4 + i * 4, y, 26), palette[i]);
        }

        return chunk;
    }
}
