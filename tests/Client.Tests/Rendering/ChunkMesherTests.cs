using System.Numerics;
using Client.Rendering;
using Shared.Blocks;
using Shared.Voxels;

namespace Client.Tests.Rendering;

public class ChunkMesherTests
{
    private const int FaceArea = ChunkMath.Size * ChunkMath.Size; // 1024 грани на сторону сплошного чанка

    private readonly BaseBlocks _blocks;
    private readonly BlockVisuals _visuals;
    private readonly ChunkMesher _mesher;
    private readonly ChunkMeshData _output = new();

    public ChunkMesherTests()
    {
        var registry = new BlockRegistry();
        _blocks = BaseBlocks.Register(registry);
        registry.Freeze();
        _visuals = new BlockVisuals(registry, BlockVisuals.BaseAppearances);
        _mesher = new ChunkMesher(_visuals);
    }

    [Fact]
    public void EmptyChunk_HasNoFaces()
    {
        _mesher.Build(new Chunk(default), default, _output);

        Assert.True(_output.IsEmpty);
        Assert.Empty(_output.Indices);
    }

    [Fact]
    public void SingleBlock_HasSixFaces()
    {
        var chunk = new Chunk(default);
        chunk.Set(new LocalPos(5, 5, 5), _blocks.Stone);

        _mesher.Build(chunk, default, _output);

        Assert.Equal(6, _output.QuadCount);
        Assert.Equal(24, _output.Vertices.Count);
        Assert.Equal(36, _output.Indices.Count);
    }

    [Fact]
    public void TwoAdjacentBlocks_HideSharedFaces()
    {
        var chunk = new Chunk(default);
        chunk.Set(new LocalPos(5, 5, 5), _blocks.Stone);
        chunk.Set(new LocalPos(6, 5, 5), _blocks.Dirt);

        _mesher.Build(chunk, default, _output);

        Assert.Equal(10, _output.QuadCount);
    }

    [Fact]
    public void FullChunk_WithoutNeighbors_HasOnlyOuterFaces()
    {
        var chunk = CreateFullChunk(default);

        _mesher.Build(chunk, default, _output);

        Assert.Equal(6 * FaceArea, _output.QuadCount);
    }

    [Fact]
    public void FullChunk_WithFullNeighbor_HidesBorderFaces()
    {
        var chunk = CreateFullChunk(default);
        var east = CreateFullChunk(new ChunkPos(1, 0, 0));

        _mesher.Build(chunk, new ChunkNeighbors(null, east, null, null, null, null), _output);

        Assert.Equal(5 * FaceArea, _output.QuadCount);
    }

    [Theory]
    [InlineData(31, 5, 5, 0, 5, 5)]   // +X: блок x = 31, сосед с x = 0
    [InlineData(0, 5, 5, 31, 5, 5)]   // −X
    [InlineData(5, 31, 5, 5, 0, 5)]   // +Y
    [InlineData(5, 0, 5, 5, 31, 5)]   // −Y
    [InlineData(5, 5, 31, 5, 5, 0)]   // +Z
    [InlineData(5, 5, 0, 5, 5, 31)]   // −Z
    public void BlockOnBorder_IsHiddenByBlockInNeighborChunk(int x, int y, int z, int nx, int ny, int nz)
    {
        var chunk = new Chunk(default);
        chunk.Set(new LocalPos(x, y, z), _blocks.Stone);
        var neighbor = new Chunk(default);
        neighbor.Set(new LocalPos(nx, ny, nz), _blocks.Stone);

        // Один и тот же чанк-сосед подставлен со всех сторон: закрыть грань может только блок напротив.
        _mesher.Build(chunk, new ChunkNeighbors(neighbor, neighbor, neighbor, neighbor, neighbor, neighbor), _output);

        Assert.Equal(5, _output.QuadCount);
    }

    [Fact]
    public void Triangles_AreCounterClockwise_WhenSeenFromOutside()
    {
        var chunk = new Chunk(default);
        var center = new Vector3(10.5f, 10.5f, 10.5f);
        chunk.Set(new LocalPos(10, 10, 10), _blocks.Stone);

        _mesher.Build(chunk, default, _output);

        for (var i = 0; i < _output.Indices.Count; i += 3)
        {
            var a = _output.Vertices[(int)_output.Indices[i]].Position;
            var b = _output.Vertices[(int)_output.Indices[i + 1]].Position;
            var c = _output.Vertices[(int)_output.Indices[i + 2]].Position;

            // При обходе против часовой стрелки нормаль (B − A) × (C − A) смотрит наружу, от центра блока.
            var normal = Vector3.Cross(b - a, c - a);
            var outward = (a + b + c) / 3f - center;
            Assert.True(Vector3.Dot(normal, outward) > 0, $"Треугольник {i / 3} смотрит внутрь блока");
        }
    }

    [Fact]
    public void Vertices_StayWithinChunkBounds()
    {
        var chunk = CreateFullChunk(default);

        _mesher.Build(chunk, default, _output);

        Assert.All(_output.Vertices, v =>
        {
            Assert.InRange(v.Position.X, 0f, ChunkMath.Size);
            Assert.InRange(v.Position.Y, 0f, ChunkMath.Size);
            Assert.InRange(v.Position.Z, 0f, ChunkMath.Size);
        });
    }

    [Fact]
    public void Build_ClearsPreviousResult()
    {
        var chunk = new Chunk(default);
        chunk.Set(new LocalPos(1, 1, 1), _blocks.Stone);

        _mesher.Build(chunk, default, _output);
        _mesher.Build(chunk, default, _output);

        Assert.Equal(6, _output.QuadCount);
    }

    [Fact]
    public void Quads_UseLayerOfTheirFace()
    {
        var chunk = new Chunk(default);
        chunk.Set(new LocalPos(5, 5, 5), _blocks.Grass);

        _mesher.Build(chunk, default, _output);

        // Порядок граней в меше совпадает с BlockFace.All, у каждой 4 вершины.
        for (var f = 0; f < BlockFace.All.Length; f++)
        {
            var expected = _visuals.GetLayer(_blocks.Grass, f);
            for (var v = 0; v < 4; v++)
                Assert.Equal(expected, _output.Vertices[f * 4 + v].Layer);
        }
    }

    private Chunk CreateFullChunk(ChunkPos position)
    {
        var chunk = new Chunk(position);
        for (var i = 0; i < ChunkMath.Volume; i++)
            chunk.Set(LocalPos.FromIndex(i), _blocks.Stone);
        return chunk;
    }
}
