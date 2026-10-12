using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Client.Rendering;

/// <summary>Шесть соседних чанков по граням. Отсутствующий сосед (null) считается воздухом.</summary>
internal readonly record struct ChunkNeighbors(
    Chunk? NegX, Chunk? PosX,
    Chunk? NegY, Chunk? PosY,
    Chunk? NegZ, Chunk? PosZ);

/// <summary>Результат построения меша. Переиспользуется между чанками, чтобы не аллоцировать списки заново.</summary>
internal sealed class ChunkMeshData
{
    public List<ChunkVertex> Vertices { get; } = [];
    public List<uint> Indices { get; } = [];

    public int QuadCount => Vertices.Count / 4;
    public bool IsEmpty => Vertices.Count == 0;

    public void Clear()
    {
        Vertices.Clear();
        Indices.Clear();
    }
}

/// <summary>
/// Строит меш чанка с отсечением скрытых граней: грань добавляется, только если соседний блок не непрозрачный.
/// Вершины в локальных координатах чанка [0; 32]; смещение чанка в мире задаёт матрица модели.
/// </summary>
internal sealed class ChunkMesher(BlockVisuals visuals)
{
    public void Build(Chunk chunk, in ChunkNeighbors neighbors, ChunkMeshData output)
    {
        output.Clear();
        if (chunk.IsEmpty)
            return;

        var blocks = chunk.Blocks;
        for (var i = 0; i < blocks.Length; i++)
        {
            var block = blocks[i];
            if (block.IsAir)
                continue;

            var pos = LocalPos.FromIndex(i);
            var origin = new Vector3(pos.X, pos.Y, pos.Z);

            for (var f = 0; f < BlockFace.All.Length; f++)
            {
                ref readonly var face = ref BlockFace.All[f];
                var neighbor = GetBlock(blocks, neighbors, pos.X + face.Dx, pos.Y + face.Dy, pos.Z + face.Dz);
                if (visuals.IsOpaque(neighbor))
                    continue;

                AddQuad(output, origin, face, visuals.GetLayer(block, f));
            }
        }
    }

    /// <summary>Блок по локальным координатам, которые могут выйти за чанк на 1 по одной оси.</summary>
    private static BlockId GetBlock(ReadOnlySpan<BlockId> blocks, in ChunkNeighbors neighbors, int x, int y, int z)
    {
        var local = new LocalPos(x, y, z);
        if (local.IsInBounds)
            return blocks[local.Index];

        var neighbor =
            x < 0 ? neighbors.NegX :
            x >= ChunkMath.Size ? neighbors.PosX :
            y < 0 ? neighbors.NegY :
            y >= ChunkMath.Size ? neighbors.PosY :
            z < 0 ? neighbors.NegZ :
            neighbors.PosZ;

        // Маска переводит −1 в 31 и 32 в 0: та же позиция в системе координат соседа.
        return neighbor?.Get(new LocalPos(x & ChunkMath.Mask, y & ChunkMath.Mask, z & ChunkMath.Mask)) ?? BlockId.Air;
    }

    private static void AddQuad(ChunkMeshData output, Vector3 origin, in BlockFace face, int layer)
    {
        var first = (uint)output.Vertices.Count;

        output.Vertices.Add(new ChunkVertex(origin + face.A, face.UvA, layer, face.Brightness));
        output.Vertices.Add(new ChunkVertex(origin + face.B, face.UvB, layer, face.Brightness));
        output.Vertices.Add(new ChunkVertex(origin + face.C, face.UvC, layer, face.Brightness));
        output.Vertices.Add(new ChunkVertex(origin + face.D, face.UvD, layer, face.Brightness));

        // Два треугольника с тем же обходом, что у углов грани: (A, B, C) и (A, C, D).
        output.Indices.Add(first);
        output.Indices.Add(first + 1);
        output.Indices.Add(first + 2);
        output.Indices.Add(first);
        output.Indices.Add(first + 2);
        output.Indices.Add(first + 3);
    }
}
