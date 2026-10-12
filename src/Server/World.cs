using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Server;

/// <summary>Мир на сервере. Пока фиксированного размера и целиком в памяти; бесконечный мир — этап 2.</summary>
public sealed class World
{
    /// <summary>Границы мира в чанках, включительно: 8×3×8 чанков, по X и Z центр в начале координат.</summary>
    public static readonly ChunkPos MinChunk = new(-4, 0, -4);
    public static readonly ChunkPos MaxChunk = new(3, 2, 3);

    public ChunkMap Chunks { get; } = new();

    internal World(BaseBlocks blocks)
    {
        var generator = new TerrainGenerator(blocks);
        for (var x = MinChunk.X; x <= MaxChunk.X; x++)
        for (var y = MinChunk.Y; y <= MaxChunk.Y; y++)
        for (var z = MinChunk.Z; z <= MaxChunk.Z; z++)
            Chunks.Set(generator.Generate(new ChunkPos(x, y, z)));
    }

    /// <summary>Точка появления: над поверхностью в центре мира, с запасом для обзора.</summary>
    public Vector3 SpawnPosition => new(0.5f, TerrainGenerator.SurfaceHeight(0, 0) + 12f, 0.5f);
}
