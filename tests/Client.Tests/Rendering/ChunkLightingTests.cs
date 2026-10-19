using Client.Rendering;
using Shared.Blocks;
using Shared.Content;
using Shared.Lighting;
using Shared.Voxels;

namespace Client.Tests.Rendering;

/// <summary>
/// Плавный свет и затенение углов в мешере. Главный предмет — верхняя грань блока (5, 5, 5) на каменном полу:
/// её вершины в углах (x, z) ∈ {5, 6}², а блоки, сходящиеся в углу, — на высоте 6 вокруг (5, 6, 5).
/// </summary>
public class ChunkLightingTests
{
    private const int Floor = 5;
    private const int Above = Floor + 1;

    private readonly BaseBlocks _blocks;
    private readonly ChunkMesher _mesher;
    private readonly ChunkMeshData _output = new();
    private readonly Chunk _chunk = new(default);

    public ChunkLightingTests()
    {
        var content = GameContent.Load();
        _blocks = BaseBlocks.From(content.Blocks);
        _mesher = new ChunkMesher(new BlockVisuals(content.Blocks, BlockVisuals.AppearancesFrom(content.BlockTextures)));
        _chunk.Light.Fill(ChunkLight.Pack(ChunkLight.Max, 0));
        for (var x = 0; x < 12; x++)
        for (var z = 0; z < 12; z++)
        {
            _chunk.Set(new LocalPos(x, Floor, z), _blocks.Stone);
            _chunk.Light.SetSky(new LocalPos(x, Floor, z).Index, 0);
        }
    }

    private void Block(int x, int y, int z)
    {
        _chunk.Set(new LocalPos(x, y, z), _blocks.Stone);
        _chunk.Light.SetSky(new LocalPos(x, y, z).Index, 0);
    }

    private void Light(int x, int y, int z, int sky, int block) => _chunk.Light.Set(new LocalPos(x, y, z).Index, ChunkLight.Pack(sky, block));

    /// <summary>Верхняя грань блока пола (<paramref name="x"/>, 5, <paramref name="z"/>): номер квадрата в меше.</summary>
    private int TopQuad(int x, int z)
    {
        for (var q = 0; q < _output.QuadCount; q++)
        {
            var vertices = Enumerable.Range(q * 4, 4).Select(i => _output.Vertices[i].Position).ToList();
            if (vertices.All(p => p.Y == Above) && vertices.Min(p => p.X) == x && vertices.Min(p => p.Z) == z)
                return q;
        }
        throw new InvalidOperationException($"Нет верхней грани блока ({x}, {Floor}, {z})");
    }

    /// <summary>Вершина верхней грани блока (5, 5, 5) в углу (<paramref name="x"/>, <paramref name="z"/>).</summary>
    private ChunkVertex Corner(int x, int z)
    {
        var q = TopQuad(5, 5);
        return Enumerable.Range(q * 4, 4).Select(i => _output.Vertices[i]).Single(v => v.Position.X == x && v.Position.Z == z);
    }

    private void Build() => _mesher.Build(_chunk, default, _output);

    // --- Затенение углов ---

    [Fact]
    public void OpenCorners_AreNotShaded()
    {
        Build();

        foreach (var (x, z) in new[] { (5, 5), (5, 6), (6, 5), (6, 6) })
            Assert.Equal(1f, Corner(x, z).Shade);
    }

    [Fact]
    public void OneSide_ShadesItsTwoCorners()
    {
        Block(4, Above, 5); // сбоку по −X от блока над гранью

        Build();

        Assert.Equal(ChunkMesher.OcclusionShade[2], Corner(5, 5).Shade);
        Assert.Equal(ChunkMesher.OcclusionShade[2], Corner(5, 6).Shade);
        Assert.Equal(1f, Corner(6, 5).Shade);
        Assert.Equal(1f, Corner(6, 6).Shade);
    }

    [Fact]
    public void DiagonalOnly_ShadesOneCorner()
    {
        Block(4, Above, 4);

        Build();

        Assert.Equal(ChunkMesher.OcclusionShade[2], Corner(5, 5).Shade);
        Assert.Equal(1f, Corner(5, 6).Shade);
        Assert.Equal(1f, Corner(6, 5).Shade);
    }

    [Fact]
    public void SideAndDiagonal_ShadeCornerTwice()
    {
        Block(4, Above, 5);
        Block(4, Above, 4);

        Build();

        Assert.Equal(ChunkMesher.OcclusionShade[1], Corner(5, 5).Shade);
        Assert.Equal(ChunkMesher.OcclusionShade[2], Corner(5, 6).Shade);
    }

    [Fact]
    public void BothSides_CloseCornerFully_EvenWithOpenDiagonal()
    {
        Block(4, Above, 5);
        Block(5, Above, 4);

        Build();

        Assert.Equal(ChunkMesher.OcclusionShade[0], Corner(5, 5).Shade);
    }

    [Fact]
    public void Shade_IsFaceBrightnessTimesOcclusion()
    {
        // Боковая грань: восточная грань блока (11, 5, 5) на краю пола, её постоянная яркость 0,6.
        Block(12, Floor - 1, 5); // под блоком перед гранью — нижний сосед угла
        Build();

        var east = BlockFace.All[0];
        var quad = Enumerable.Range(0, _output.QuadCount)
            .Single(q => Enumerable.Range(q * 4, 4).Select(i => _output.Vertices[i].Position)
                .All(p => p.X == 12 && p.Y is Floor or Above && p.Z is 5 or 6));
        var vertices = Enumerable.Range(quad * 4, 4).Select(i => _output.Vertices[i]).ToList();
        Assert.All(vertices.Where(v => v.Position.Y == Above), v => Assert.Equal(east.Brightness, v.Shade));
        Assert.All(vertices.Where(v => v.Position.Y == Floor), v => Assert.Equal(east.Brightness * ChunkMesher.OcclusionShade[2], v.Shade));
    }

    // --- Плавный свет ---

    [Fact]
    public void CornerLight_IsAverageOfFourBlocksInFront()
    {
        Light(5, Above, 5, sky: 15, block: 2);
        Light(4, Above, 5, sky: 11, block: 6);
        Light(5, Above, 4, sky: 7, block: 10);
        Light(4, Above, 4, sky: 3, block: 14);

        Build();

        Assert.Equal((15 + 11 + 7 + 3) / 4f, Corner(5, 5).Sky);
        Assert.Equal((2 + 6 + 10 + 14) / 4f, Corner(5, 5).BlockLight);
    }

    [Fact]
    public void CornerLight_SkipsOpaqueBlocks()
    {
        Light(5, Above, 5, sky: 15, block: 0);
        Block(4, Above, 5);
        Light(5, Above, 4, sky: 7, block: 0);
        Light(4, Above, 4, sky: 2, block: 0);

        Build();

        Assert.Equal((15 + 7 + 2) / 3f, Corner(5, 5).Sky);
    }

    [Fact]
    public void CornerLight_BehindTwoSides_IsNotTaken()
    {
        // Свет за стыком двух непрозрачных блоков в угол не протекает.
        Light(5, Above, 5, sky: 6, block: 0);
        Block(4, Above, 5);
        Block(5, Above, 4);
        Light(4, Above, 4, sky: 15, block: 15);

        Build();

        Assert.Equal(6f, Corner(5, 5).Sky);
        Assert.Equal(0f, Corner(5, 5).BlockLight);
    }

    // --- Диагональ квадрата ---

    /// <summary>Индексы двух треугольников квадрата относительно его первой вершины.</summary>
    private uint[] TriangleIndices(int quad) => [.. _output.Indices.Skip(quad * 6).Take(6).Select(i => i - (uint)(quad * 4))];

    [Fact]
    public void DarkCornerA_SplitsAlongOtherDiagonal()
    {
        // Угол A верхней грани — (5, 5): закрыт полностью; перепад по A–C большой, по B–D нет.
        Block(4, Above, 5);
        Block(5, Above, 4);

        Build();

        Assert.Equal([0u, 1u, 3u, 1u, 2u, 3u], TriangleIndices(TopQuad(5, 5)));
    }

    [Fact]
    public void DarkCornerB_KeepsDiagonalAC()
    {
        // Угол B верхней грани — (5, 6).
        Block(4, Above, 5);
        Block(5, Above, 6);

        Build();

        Assert.Equal([0u, 1u, 2u, 0u, 2u, 3u], TriangleIndices(TopQuad(5, 5)));
    }

    [Fact]
    public void DarkLight_AlsoChoosesDiagonal()
    {
        // Без затенения: свет у угла A слабее, чем у остальных, — диагональ та же, что у тёмного угла.
        Light(4, Above, 4, sky: 0, block: 0);
        Light(4, Above, 5, sky: 0, block: 0);
        Light(5, Above, 4, sky: 0, block: 0);

        Build();

        Assert.Equal([0u, 1u, 3u, 1u, 2u, 3u], TriangleIndices(TopQuad(5, 5)));
    }

    // --- Граница чанков ---

    [Fact]
    public void ChunkBorder_HasNoSeam()
    {
        // Пол через границу двух чанков, неровный свет над ним и камни у границы: вершины в одной точке мира
        // у мешей обоих чанков одинаковы — по свету и по затенению. Кроме углов, закрытых двумя боковыми блоками:
        // к такой точке подходят грани по разные стороны каменного стыка, и у каждой свой свет (так и задумано).
        var west = new Chunk(new ChunkPos(0, 0, 0));
        var east = new Chunk(new ChunkPos(1, 0, 0));
        var random = new Random(3);
        foreach (var chunk in new[] { west, east })
        {
            for (var x = 0; x < ChunkMath.Size; x++)
            for (var z = 0; z < ChunkMath.Size; z++)
            {
                chunk.Set(new LocalPos(x, Floor, z), _blocks.Stone);
                for (var y = Above; y < Above + 3; y++)
                    chunk.Light.Set(new LocalPos(x, y, z).Index, ChunkLight.Pack(random.Next(16), random.Next(16)));
                if (x is 0 or 1 or 30 or 31 && random.Next(4) == 0)
                    chunk.Set(new LocalPos(x, Above, z), _blocks.Stone);
            }
        }

        var points = new Dictionary<(float X, float Y, float Z), ((float Shade, float Sky, float Block) Value, Chunk From)>();
        var compared = 0;
        var shaded = 0;
        foreach (var (chunk, neighbors, shift) in new[]
        {
            (west, new ChunkNeighbors(null, east, null, null, null, null), 0f),
            (east, new ChunkNeighbors(west, null, null, null, null, null), ChunkMath.Size),
        })
        {
            _mesher.Build(chunk, neighbors, _output);
            for (var q = 0; q < _output.QuadCount; q++)
            {
                var quad = Enumerable.Range(q * 4, 4).Select(i => _output.Vertices[i]).ToList();
                if (!quad.All(v => v.Position.Y == Above))
                    continue; // только верх пола
                foreach (var v in quad.Where(v => v.Position.X + shift == ChunkMath.Size && v.Shade != ChunkMesher.OcclusionShade[0]))
                {
                    var key = (v.Position.X + shift, v.Position.Y, v.Position.Z);
                    var value = (v.Shade, v.Sky, v.BlockLight);
                    if (points.TryGetValue(key, out var seen))
                    {
                        Assert.Equal(seen.Value, value);
                        if (seen.From != chunk)
                        {
                            compared++;
                            shaded += v.Shade < 1f ? 1 : 0;
                        }
                    }
                    else
                    {
                        points[key] = (value, chunk);
                    }
                }
            }
        }
        Assert.True(compared >= 20, $"Сравнено всего {compared} вершин на границе");
        Assert.True(shaded >= 3, $"Среди сравненных затенённых всего {shaded}");
    }
}
