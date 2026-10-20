using System.Numerics;
using Client.Rendering;
using Shared.Blocks;
using Shared.Content;
using Shared.Lighting;
using Shared.Voxels;

namespace Client.Tests.Rendering;

/// <summary>
/// Модель факела в мешере (шаг 8.5): факел (5, 6, 5) стоит на камне (5, 5, 5), рядом по +X — камень (6, 6, 5).
/// Столбик — 2 × 10 × 2 пикселя в центре блока.
/// </summary>
public class TorchMeshTests
{
    private const float Pixel = 1 / 16f;
    private static readonly Vector3 Min = new(5 + 7 * Pixel, 6, 5 + 7 * Pixel);
    private static readonly Vector3 Max = new(5 + 9 * Pixel, 6 + 10 * Pixel, 5 + 9 * Pixel);

    private readonly BlockVisuals _visuals;
    private readonly ChunkMesher _mesher;
    private readonly ChunkMeshData _output = new();
    private readonly Chunk _chunk = new(default);
    private readonly BlockId _torch;

    public TorchMeshTests()
    {
        var content = GameContent.Load();
        _visuals = new BlockVisuals(content.Blocks, BlockVisuals.AppearancesFrom(content.BlockTextures), content.BlockModels);
        _mesher = new ChunkMesher(_visuals);
        _torch = content.Blocks.Get("base:torch").Id;
        var stone = content.Blocks.Get("base:stone").Id;
        _chunk.Light.Fill(ChunkLight.Pack(ChunkLight.Max, 0));
        _chunk.Set(new LocalPos(5, 5, 5), stone);
        _chunk.Set(new LocalPos(6, 6, 5), stone);
        _chunk.Set(new LocalPos(5, 6, 5), _torch);
        _chunk.Light.Set(new LocalPos(5, 6, 5).Index, ChunkLight.Pack(ChunkLight.Max, 14));
        _mesher.Build(_chunk, default, _output);
    }

    private IEnumerable<ChunkVertex[]> Quads() =>
        Enumerable.Range(0, _output.QuadCount).Select(q => _output.Vertices.Skip(q * 4).Take(4).ToArray());

    private static bool Inside(Vector3 p) =>
        p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;

    /// <summary>Квадраты столбика: все вершины внутри него.</summary>
    private List<ChunkVertex[]> TorchQuads() => Quads().Where(q => q.All(v => Inside(v.Position))).ToList();

    [Fact]
    public void Torch_IsColumn_FourSidesAndTop_NoBottom()
    {
        var quads = TorchQuads();

        Assert.Equal(5, quads.Count);
        // Верх — на 10 пикселях, низа нет: он стоит на опоре.
        Assert.Single(quads, q => q.All(v => v.Position.Y == Max.Y));
        Assert.DoesNotContain(quads, q => q.All(v => v.Position.Y == Min.Y));
        // Четыре боковые — по одной на каждую сторону столбика, во всю его высоту.
        var sides = quads.Where(q => q.Any(v => v.Position.Y == Min.Y)).ToList();
        Assert.Equal(4, sides.Count);
        Assert.All(sides, q => Assert.Equal((Min.Y, Max.Y), (q.Min(v => v.Position.Y), q.Max(v => v.Position.Y))));
        Assert.Single(sides, q => q.All(v => v.Position.X == Max.X));
        Assert.Single(sides, q => q.All(v => v.Position.X == Min.X));
        Assert.Single(sides, q => q.All(v => v.Position.Z == Max.Z));
        Assert.Single(sides, q => q.All(v => v.Position.Z == Min.Z));
        // Каждая грань — та часть текстуры факела, что приходится на её место.
        Assert.All(quads, q => Assert.All(q, v => Assert.Equal(_visuals.GetLayer(_torch, 0), v.Layer)));
    }

    /// <summary>Бока берут из текстуры столбцы 7–8 и строки 6–15, верх — строки 7–8 тех же столбцов (пламя).</summary>
    [Fact]
    public void Torch_Uv_IsCentralColumnOfTexture()
    {
        foreach (var quad in TorchQuads())
        {
            var top = quad.All(v => v.Position.Y == Max.Y);
            Assert.Equal((7 * Pixel, 9 * Pixel), (quad.Min(v => v.Uv.X), quad.Max(v => v.Uv.X)));
            Assert.Equal(top ? (7 * Pixel, 9 * Pixel) : (6 * Pixel, 1f), (quad.Min(v => v.Uv.Y), quad.Max(v => v.Uv.Y)));
        }
    }

    /// <summary>Свет — своего блока, без затенения углов: множитель — только постоянная яркость грани.</summary>
    [Fact]
    public void Torch_Light_IsOwnBlock_WithoutOcclusion()
    {
        foreach (var quad in TorchQuads())
        {
            var brightness = quad.All(v => v.Position.Y == Max.Y) ? 1f : quad.All(v => v.Position.X == quad[0].Position.X) ? 0.6f : 0.8f;
            Assert.All(quad, v => Assert.Equal((15f, 14f, brightness), (v.Sky, v.BlockLight, v.Shade)));
        }
    }

    /// <summary>Факел тоньше блока и соседей не закрывает: верх опоры и бок камня рядом рисуются.</summary>
    [Fact]
    public void Torch_DoesNotHideNeighbourFaces()
    {
        Assert.Contains(Quads(), q => q.All(v => v.Position.Y == 6) && q.Min(v => v.Position.X) == 5 && q.Max(v => v.Position.X) == 6
            && q.Min(v => v.Position.Z) == 5);
        Assert.Contains(Quads(), q => q.All(v => v.Position.X == 6) && q.Min(v => v.Position.Y) == 6 && q.Min(v => v.Position.Z) == 5);
    }
}
