using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Client.Rendering;

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
/// <remarks>
/// <para>
/// Свет плавный (smooth lighting): у каждой вершины грани — среднее света четырёх блоков перед гранью, сходящихся
/// в этом углу (прямо перед гранью, два сбоку и один по диагонали), — только прозрачных. Затенение угла (ambient
/// occlusion) — по трём из них, кроме переднего: два боковых непрозрачных — угол тёмный полностью, а диагональный
/// тогда не учитывается и в свете (иначе свет протекал бы сквозь стык двух блоков).
/// </para>
/// <para>
/// Квадрат делится на два треугольника по той диагонали, вдоль которой перепад яркости меньше: значения внутри
/// треугольника смешиваются линейно, и при неудачной диагонали тёмный угол тянулся бы полосой через всю грань.
/// </para>
/// <para>
/// Блоки не-кубы (<see cref="BlockModel"/>) строятся своими гранями: факел — столбик в центре блока.
/// </para>
/// </remarks>
internal sealed class ChunkMesher(BlockVisuals visuals)
{
    /// <summary>Множитель яркости вершины по затенению угла: 0 — угол закрыт со всех сторон, 3 — открыт.</summary>
    internal static readonly float[] OcclusionShade = [0.5f, 0.65f, 0.8f, 1f];

    /// <summary>Сдвиг индекса в снимке к соседу по каждой грани (в порядке <see cref="BlockFace.All"/>).</summary>
    private static readonly int[] FaceOffsets = BlockFace.All
        .Select(f => ChunkSnapshot.Index(f.Dx, f.Dy, f.Dz) - ChunkSnapshot.Index(0, 0, 0))
        .ToArray();

    /// <summary>
    /// Для каждой грани и её вершины (A, B, C, D) — сдвиги от блока перед гранью к двум боковым и диагональному блоку,
    /// сходящимся в этом углу.
    /// </summary>
    private static readonly (int Side1, int Side2, int Corner)[][] CornerOffsets = BlockFace.All.Select(CornersOf).ToArray();

    private static (int Side1, int Side2, int Corner)[] CornersOf(BlockFace face)
    {
        // Две оси в плоскости грани; угол в [0; 1]³ задаёт, в какую сторону по каждой из них смотреть.
        var normal = face.Normal;
        Vector3 u = normal.X != 0 ? Vector3.UnitY : Vector3.UnitX;
        Vector3 v = normal.Z != 0 ? Vector3.UnitY : Vector3.UnitZ;
        return [.. new[] { face.A, face.B, face.C, face.D }.Select(corner =>
        {
            var su = Vector3.Dot(corner, u) > 0.5f ? 1 : -1;
            var sv = Vector3.Dot(corner, v) > 0.5f ? 1 : -1;
            var side1 = u * su;
            var side2 = v * sv;
            return (Offset(side1), Offset(side2), Offset(side1 + side2));
        })];
    }

    private static int Offset(Vector3 d) => ChunkSnapshot.Index((int)d.X, (int)d.Y, (int)d.Z) - ChunkSnapshot.Index(0, 0, 0);

    /// <summary>Столбик факела в блоке: от угла <see cref="TorchMin"/> размером <see cref="TorchSize"/> (2 × 10 × 2 пикселя из 16).</summary>
    internal static readonly Vector3 TorchMin = new(7 / 16f, 0, 7 / 16f);
    internal static readonly Vector3 TorchSize = new(2 / 16f, 10 / 16f, 2 / 16f);

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
        Span<ChunkVertex> quad = stackalloc ChunkVertex[4];
        Span<float> brightness = stackalloc float[4];
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
                if (visuals.Model(block) == BlockModel.Torch)
                {
                    AddTorch(snapshot, index, origin, block, output);
                    continue;
                }

                for (var f = 0; f < FaceOffsets.Length; f++)
                {
                    // Рамка снимка содержит соседние блоки, поэтому выхода за массив нет.
                    var front = index + FaceOffsets[f];
                    if (visuals.IsOpaque(blocks[front]))
                        continue;

                    ref readonly var face = ref BlockFace.All[f];
                    var layer = visuals.GetLayer(block, f);
                    var corners = CornerOffsets[f];
                    quad[0] = Vertex(snapshot, front, corners[0], origin + face.A, face.UvA, layer, face.Brightness, out brightness[0]);
                    quad[1] = Vertex(snapshot, front, corners[1], origin + face.B, face.UvB, layer, face.Brightness, out brightness[1]);
                    quad[2] = Vertex(snapshot, front, corners[2], origin + face.C, face.UvC, layer, face.Brightness, out brightness[2]);
                    quad[3] = Vertex(snapshot, front, corners[3], origin + face.D, face.UvD, layer, face.Brightness, out brightness[3]);
                    AddQuad(output, quad, MathF.Abs(brightness[0] - brightness[2]) > MathF.Abs(brightness[1] - brightness[3]));
                }
            }
        }
    }

    /// <param name="front">Блок перед гранью (в индексах снимка).</param>
    /// <param name="brightness">Яркость вершины для выбора диагонали: затенение × свет (как в полдень).</param>
    private ChunkVertex Vertex(ChunkSnapshot snapshot, int front, (int Side1, int Side2, int Corner) corner,
        Vector3 position, Vector2 uv, int layer, float faceBrightness, out float brightness)
    {
        var blocks = snapshot.Blocks;
        var light = snapshot.Light;
        var side1 = front + corner.Side1;
        var side2 = front + corner.Side2;
        var diagonal = front + corner.Corner;
        var open1 = !visuals.IsOpaque(blocks[side1]);
        var open2 = !visuals.IsOpaque(blocks[side2]);
        // Два боковых закрыты — угол закрыт целиком (затенение 0), и свет из-за стыка не берётся.
        var openCorner = (open1 || open2) && !visuals.IsOpaque(blocks[diagonal]);
        var occlusion = (open1 ? 1 : 0) + (open2 ? 1 : 0) + (openCorner ? 1 : 0);

        // Блок перед гранью всегда прозрачный: иначе грани не было бы.
        int sky = light[front] >> 4, block = light[front] & 0x0F, count = 1;
        if (open1)
            Add(light[side1], ref sky, ref block, ref count);
        if (open2)
            Add(light[side2], ref sky, ref block, ref count);
        if (openCorner)
            Add(light[diagonal], ref sky, ref block, ref count);

        float skyLevel = (float)sky / count, blockLevel = (float)block / count;
        var shade = OcclusionShade[occlusion];
        brightness = shade * MathF.Max(skyLevel, blockLevel);
        return new ChunkVertex(position, uv, layer, faceBrightness * shade, skyLevel, blockLevel);
    }

    /// <summary>
    /// Факел: столбик тоньше блока, поэтому соседи его граней не закрывают — видны все, кроме нижней (она стоит на опоре).
    /// Свет — своего блока (факел светится сам), без затенения углов: углы столбика не касаются соседних блоков.
    /// </summary>
    private void AddTorch(ChunkSnapshot snapshot, int index, Vector3 origin, BlockId block, ChunkMeshData output)
    {
        var packed = snapshot.Light[index];
        float sky = packed >> 4, blockLight = packed & 0x0F;
        Span<ChunkVertex> quad = stackalloc ChunkVertex[4];
        for (var f = 0; f < BlockFace.All.Length; f++)
        {
            ref readonly var face = ref BlockFace.All[f];
            if (face.Dy < 0)
                continue;

            var layer = visuals.GetLayer(block, f);
            quad[0] = TorchVertex(face, face.A, origin, layer, sky, blockLight);
            quad[1] = TorchVertex(face, face.B, origin, layer, sky, blockLight);
            quad[2] = TorchVertex(face, face.C, origin, layer, sky, blockLight);
            quad[3] = TorchVertex(face, face.D, origin, layer, sky, blockLight);
            AddQuad(output, quad, flip: false);
        }
    }

    /// <summary>Угол грани факела: угол единичного куба, сжатый до столбика; UV — та часть текстуры, что приходится на это место.</summary>
    private static ChunkVertex TorchVertex(in BlockFace face, Vector3 corner, Vector3 origin, int layer, float sky, float blockLight)
    {
        var point = TorchMin + corner * TorchSize;
        return new ChunkVertex(origin + point, face.UvAt(point), layer, face.Brightness, sky, blockLight);
    }

    private static void Add(byte packed, ref int sky, ref int block, ref int count)
    {
        sky += packed >> 4;
        block += packed & 0x0F;
        count++;
    }

    /// <param name="flip">Делить по диагонали B–D, а не A–C.</param>
    private static void AddQuad(ChunkMeshData output, ReadOnlySpan<ChunkVertex> quad, bool flip)
    {
        var first = (uint)output.Vertices.Count;
        foreach (var vertex in quad)
            output.Vertices.Add(vertex);

        // Два треугольника с тем же обходом, что у углов грани: (A, B, C) и (A, C, D) либо (A, B, D) и (B, C, D).
        if (flip)
        {
            output.Indices.Add(first);
            output.Indices.Add(first + 1);
            output.Indices.Add(first + 3);
            output.Indices.Add(first + 1);
            output.Indices.Add(first + 2);
            output.Indices.Add(first + 3);
        }
        else
        {
            output.Indices.Add(first);
            output.Indices.Add(first + 1);
            output.Indices.Add(first + 2);
            output.Indices.Add(first);
            output.Indices.Add(first + 2);
            output.Indices.Add(first + 3);
        }
    }
}
