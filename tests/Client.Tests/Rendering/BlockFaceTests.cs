using System.Numerics;
using Client.Rendering;

namespace Client.Tests.Rendering;

public class BlockFaceTests
{
    public static TheoryData<int> FaceIndices() => [0, 1, 2, 3, 4, 5];

    private static (Vector3 Corner, Vector2 Uv)[] Corners(in BlockFace f) =>
        [(f.A, f.UvA), (f.B, f.UvB), (f.C, f.UvC), (f.D, f.UvD)];

    [Theory]
    [MemberData(nameof(FaceIndices))]
    public void Uv_CoversWholeTexture(int index)
    {
        var uvs = Corners(BlockFace.All[index]).Select(c => c.Uv).ToHashSet();

        // Четыре угла грани попадают ровно в четыре угла текстуры, без растяжения и повторов.
        Assert.True(uvs.SetEquals([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]), string.Join(" ", uvs));
    }

    [Theory]
    [MemberData(nameof(FaceIndices))]
    public void SideFaces_HaveTextureTopAtBlockTop(int index)
    {
        var face = BlockFace.All[index];
        if (face.Kind != FaceKind.Side)
            return;

        // v = 0 — верх картинки: у верхних углов грани (y = 1), иначе полоса травы окажется снизу.
        foreach (var (corner, uv) in Corners(face))
            Assert.Equal(1 - corner.Y, uv.Y);
    }

    [Theory]
    [MemberData(nameof(FaceIndices))]
    public void SideFaces_AreNotMirrored(int index)
    {
        var face = BlockFace.All[index];
        if (face.Kind != FaceKind.Side)
            return;

        // Если смотреть на грань снаружи, u растёт слева направо. «Вправо» для наблюдателя: forward × up,
        // где forward = −нормаль. Зеркальная грань дала бы перевёрнутую по горизонтали текстуру.
        var right = Vector3.Cross(-face.Normal, Vector3.UnitY);
        foreach (var (corner, uv) in Corners(face))
        {
            var expectedU = Vector3.Dot(corner, right) - MathF.Min(0, right.X + right.Z);
            Assert.Equal(expectedU, uv.X, precision: 5);
        }
    }

    [Fact]
    public void Kinds_AreOneTopOneBottomFourSides()
    {
        var kinds = BlockFace.All.Select(f => f.Kind).ToArray();

        Assert.Single(kinds, k => k == FaceKind.Top);
        Assert.Single(kinds, k => k == FaceKind.Bottom);
        Assert.Equal(4, kinds.Count(k => k == FaceKind.Side));
    }
}
