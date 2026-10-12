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
    /// <summary>Сдвиг индекса в снимке к соседу по каждой грани (в порядке <see cref="BlockFace.All"/>).</summary>
    private static readonly int[] FaceOffsets = BlockFace.All
        .Select(f => ChunkSnapshot.Index(f.Dx, f.Dy, f.Dz) - ChunkSnapshot.Index(0, 0, 0))
        .ToArray();

    /// <summary>Строит меш прямо по чанку и соседям. Для тестов и мест, где снимок не нужен.</summary>
    public void Build(Chunk chunk, in ChunkNeighbors neighbors, ChunkMeshData output)
    {
        var snapshot = new ChunkSnapshot();
        snapshot.Capture(chunk, neighbors);
        Build(snapshot, output);
    }

    /// <summary>Строит меш по снимку. Потокобезопасен: снимок и <see cref="BlockVisuals"/> только читаются.</summary>
    public void Build(ChunkSnapshot snapshot, ChunkMeshData output)
    {
        output.Clear();
        if (snapshot.IsEmpty)
            return;

        var blocks = snapshot.Blocks;
        for (var y = 0; y < ChunkMath.Size; y++)
        for (var z = 0; z < ChunkMath.Size; z++)
        {
            var row = ChunkSnapshot.Index(0, y, z);
            for (var x = 0; x < ChunkMath.Size; x++)
            {
                var index = row + x;
                var block = blocks[index];
                if (block.IsAir)
                    continue;

                var origin = new Vector3(x, y, z);
                for (var f = 0; f < FaceOffsets.Length; f++)
                {
                    // Рамка снимка содержит соседние блоки, поэтому выхода за массив нет.
                    if (visuals.IsOpaque(blocks[index + FaceOffsets[f]]))
                        continue;

                    AddQuad(output, origin, BlockFace.All[f], visuals.GetLayer(block, f));
                }
            }
        }
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
