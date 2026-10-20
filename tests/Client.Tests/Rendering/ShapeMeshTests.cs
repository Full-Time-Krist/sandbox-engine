using System.Numerics;
using Client.Rendering;
using Shared.Blocks;
using Shared.Content;
using Shared.Lighting;
using Shared.Voxels;

namespace Client.Tests.Rendering;

/// <summary>
/// Блоки не-кубы в мешере (шаги 8.5–8.6): каждый короб формы — шесть граней. Факел (5, 6, 5) стоит на камне (5, 5, 5),
/// рядом по +X — камень (6, 6, 5); каменная плита (10, 6, 10) — на камне (10, 5, 10), над ней по +Y — камень (10, 7, 10).
/// </summary>
public class ShapeMeshTests
{
    private const float Pixel = 1 / 16f;
    private static readonly Vector3 TorchMin = new(5 + 7 * Pixel, 6, 5 + 7 * Pixel);
    private static readonly Vector3 TorchMax = new(5 + 9 * Pixel, 6 + 10 * Pixel, 5 + 9 * Pixel);
    private static readonly Vector3 SlabMin = new(10, 6, 10);
    private static readonly Vector3 SlabMax = new(11, 6.5f, 11);

    private readonly BlockVisuals _visuals;
    private readonly ChunkMeshData _output = new();
    private readonly Chunk _chunk = new(default);
    private readonly BlockId _torch;
    private readonly BlockId _slab;

    public ShapeMeshTests()
    {
        var content = GameContent.Load();
        _visuals = new BlockVisuals(content.Blocks, BlockVisuals.AppearancesFrom(content.BlockTextures));
        _torch = content.Blocks.Get("base:torch").Id;
        _slab = content.Blocks.Get("base:stone_slab").Id;
        var stone = content.Blocks.Get("base:stone").Id;
        _chunk.Light.Fill(ChunkLight.Pack(ChunkLight.Max, 0));
        _chunk.Set(new LocalPos(5, 5, 5), stone);
        _chunk.Set(new LocalPos(6, 6, 5), stone);
        _chunk.Set(new LocalPos(5, 6, 5), _torch);
        _chunk.Light.Set(new LocalPos(5, 6, 5).Index, ChunkLight.Pack(ChunkLight.Max, 14));
        _chunk.Set(new LocalPos(10, 5, 10), stone);
        _chunk.Set(new LocalPos(10, 6, 10), _slab);
        _chunk.Set(new LocalPos(10, 7, 10), stone);
        // Свет у плиты разный в своей клетке и у соседа по −X: видно, чей свет взяла грань.
        _chunk.Light.Set(new LocalPos(10, 6, 10).Index, ChunkLight.Pack(9, 0));
        _chunk.Light.Set(new LocalPos(9, 6, 10).Index, ChunkLight.Pack(12, 3));
        new ChunkMesher(_visuals).Build(_chunk, default, _output);
    }

    private IEnumerable<ChunkVertex[]> Quads() =>
        Enumerable.Range(0, _output.QuadCount).Select(q => _output.Vertices.Skip(q * 4).Take(4).ToArray());

    private static bool Inside(Vector3 p, Vector3 min, Vector3 max) =>
        p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;

    /// <summary>Квадраты внутри короба: все вершины в нём.</summary>
    private List<ChunkVertex[]> QuadsIn(Vector3 min, Vector3 max) => Quads().Where(q => q.All(v => Inside(v.Position, min, max))).ToList();

    [Fact]
    public void Torch_IsColumn_FourSidesAndTop_BottomHiddenBySupport()
    {
        var quads = QuadsIn(TorchMin, TorchMax);

        Assert.Equal(5, quads.Count);
        // Верх — на 10 пикселях, низ лежит на камне и закрыт им, как грань куба.
        Assert.Single(quads, q => q.All(v => v.Position.Y == TorchMax.Y));
        Assert.DoesNotContain(quads, q => q.All(v => v.Position.Y == TorchMin.Y));
        // Четыре боковые — по одной на каждую сторону столбика, во всю его высоту.
        var sides = quads.Where(q => q.Any(v => v.Position.Y == TorchMin.Y)).ToList();
        Assert.Equal(4, sides.Count);
        Assert.All(sides, q => Assert.Equal((TorchMin.Y, TorchMax.Y), (q.Min(v => v.Position.Y), q.Max(v => v.Position.Y))));
        Assert.Single(sides, q => q.All(v => v.Position.X == TorchMax.X));
        Assert.Single(sides, q => q.All(v => v.Position.X == TorchMin.X));
        Assert.Single(sides, q => q.All(v => v.Position.Z == TorchMax.Z));
        Assert.Single(sides, q => q.All(v => v.Position.Z == TorchMin.Z));
        Assert.All(quads, q => Assert.All(q, v => Assert.Equal(_visuals.GetLayer(_torch, 0), v.Layer)));
    }

    /// <summary>Бока берут из текстуры столбцы 7–8 и строки 6–15, верх — строки 7–8 тех же столбцов (пламя).</summary>
    [Fact]
    public void Torch_Uv_IsCentralColumnOfTexture()
    {
        foreach (var quad in QuadsIn(TorchMin, TorchMax))
        {
            var top = quad.All(v => v.Position.Y == TorchMax.Y);
            Assert.Equal((7 * Pixel, 9 * Pixel), (quad.Min(v => v.Uv.X), quad.Max(v => v.Uv.X)));
            Assert.Equal(top ? (7 * Pixel, 9 * Pixel) : (6 * Pixel, 1f), (quad.Min(v => v.Uv.Y), quad.Max(v => v.Uv.Y)));
        }
    }

    /// <summary>Грани внутри клетки освещены светом самого блока, без затенения углов: множитель — только яркость грани.</summary>
    [Fact]
    public void Torch_Light_IsOwnBlock_WithoutOcclusion()
    {
        foreach (var quad in QuadsIn(TorchMin, TorchMax))
        {
            var brightness = quad.All(v => v.Position.Y == TorchMax.Y) ? 1f : quad.All(v => v.Position.X == quad[0].Position.X) ? 0.6f : 0.8f;
            Assert.All(quad, v => Assert.Equal((15f, 14f, brightness), (v.Sky, v.BlockLight, v.Shade)));
        }
    }

    /// <summary>Не-куб соседей не закрывает: верх опоры факела и бок камня рядом рисуются.</summary>
    [Fact]
    public void Shape_DoesNotHideNeighbourFaces()
    {
        Assert.Contains(Quads(), q => q.All(v => v.Position.Y == 6) && q.Min(v => v.Position.X) == 5 && q.Max(v => v.Position.X) == 6
            && q.Min(v => v.Position.Z) == 5);
        Assert.Contains(Quads(), q => q.All(v => v.Position.X == 6) && q.Min(v => v.Position.Y) == 6 && q.Min(v => v.Position.Z) == 5);
        // Низ камня над плитой виден: между ними пол-блока воздуха.
        Assert.Contains(Quads(), q => q.All(v => v.Position.Y == 7) && q.Min(v => v.Position.X) == 10 && q.Min(v => v.Position.Z) == 10);
    }

    /// <summary>Низ плиты занимает всю сторону клетки и закрывает верх камня под ней, как закрыл бы куб.</summary>
    [Fact]
    public void SlabBottom_HidesTopOfBlockBelow()
    {
        Assert.DoesNotContain(Quads(), q => q.All(v => v.Position.Y == 6) && q.Min(v => v.Position.X) == 10 && q.Min(v => v.Position.Z) == 10);
    }

    /// <summary>
    /// Плита: пять граней (низ закрыт камнем), верх на половине высоты; бока — во всю ширину, высотой в полблока, с нижней
    /// половиной текстуры.
    /// </summary>
    [Fact]
    public void Slab_IsLowerHalf_SidesUseLowerHalfOfTexture()
    {
        var quads = QuadsIn(SlabMin, SlabMax);

        Assert.Equal(5, quads.Count);
        Assert.Single(quads, q => q.All(v => v.Position.Y == SlabMax.Y));
        Assert.DoesNotContain(quads, q => q.All(v => v.Position.Y == SlabMin.Y));
        foreach (var side in quads.Where(q => q.Any(v => v.Position.Y == SlabMin.Y)))
        {
            Assert.Equal((0.5f, 1f), (side.Min(v => v.Uv.Y), side.Max(v => v.Uv.Y)));
            Assert.Equal((0f, 1f), (side.Min(v => v.Uv.X), side.Max(v => v.Uv.X)));
        }
    }

    /// <summary>
    /// Свет граней плиты: бок на границе клетки — свет соседа перед ним (как у куба), верх внутри клетки — свет самой плиты.
    /// </summary>
    [Fact]
    public void Slab_Light_BoundaryFaceFromNeighbour_InnerFaceFromItself()
    {
        var quads = QuadsIn(SlabMin, SlabMax);

        var west = Assert.Single(quads, q => q.All(v => v.Position.X == SlabMin.X));
        Assert.All(west, v => Assert.Equal((12f, 3f), (v.Sky, v.BlockLight)));
        var top = Assert.Single(quads, q => q.All(v => v.Position.Y == SlabMax.Y));
        Assert.All(top, v => Assert.Equal((9f, 0f), (v.Sky, v.BlockLight)));
    }
}
