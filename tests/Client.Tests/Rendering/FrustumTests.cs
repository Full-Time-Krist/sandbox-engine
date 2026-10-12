using System.Numerics;
using Engine.Graphics;

namespace Client.Tests.Rendering;

public class FrustumTests
{
    /// <summary>Камера смотрит вдоль −Z (нулевые углы), поле зрения 70°, 16:9, дальняя плоскость 1000.</summary>
    private static readonly Camera Camera = new();

    private static Frustum Frustum => Frustum.FromMatrix(Camera.ViewProjection);

    private static bool Box(float x, float y, float z, float size = 1) =>
        Frustum.Intersects(new Vector3(x, y, z), new Vector3(x + size, y + size, z + size));

    [Fact]
    public void BoxInFront_IsVisible() => Assert.True(Box(-0.5f, -0.5f, -10));

    [Fact]
    public void BoxBehind_IsCulled() => Assert.False(Box(-0.5f, -0.5f, 10));

    [Fact]
    public void BoxFarToTheSide_IsCulled() => Assert.False(Box(50, 0, -10));

    [Fact]
    public void BoxAboveView_IsCulled() => Assert.False(Box(0, 50, -10));

    [Fact]
    public void BoxBeyondFarPlane_IsCulled() => Assert.False(Box(-0.5f, -0.5f, -1100));

    [Fact]
    public void BoxAroundCamera_IsVisible() => Assert.True(Box(-16, -16, -16, size: 32));

    [Fact]
    public void BoxCrossingSideEdge_IsVisible()
    {
        // Половина коробки за левым краем экрана, половина — в кадре.
        var halfWidth = MathF.Tan(Camera.FieldOfView / 2) * Camera.AspectRatio * 10;
        Assert.True(Box(-halfWidth - 1, -1, -10, size: 2));
    }

    [Fact]
    public void TurnedCamera_SeesWhatIsInFrontOfIt()
    {
        var camera = new Camera { Yaw = MathF.PI / 2 }; // взгляд вдоль +X
        var frustum = Frustum.FromMatrix(camera.ViewProjection);

        Assert.True(frustum.Intersects(new Vector3(10, -1, -1), new Vector3(12, 1, 1)));
        Assert.False(frustum.Intersects(new Vector3(-12, -1, -1), new Vector3(-10, 1, 1)));
    }

    [Fact]
    public void NeverCullsBoxWithAVisibleCorner()
    {
        // Отсечение может оставить лишнее, но не должно выбросить видимое. Проверяем по самой матрице:
        // если хоть один угол коробки попадает в объём отсечения, коробка обязана считаться видимой.
        var camera = new Camera { Yaw = 0.7f, Pitch = -0.3f };
        var matrix = camera.ViewProjection;
        var frustum = Frustum.FromMatrix(matrix);
        var random = new Random(5);

        for (var i = 0; i < 5000; i++)
        {
            var min = new Vector3(random.NextSingle() * 400 - 200, random.NextSingle() * 200 - 100, random.NextSingle() * 400 - 200);
            var max = min + new Vector3(32);

            var anyCornerInside = false;
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? min.X : max.X, (c & 2) == 0 ? min.Y : max.Y, (c & 4) == 0 ? min.Z : max.Z);
                var clip = Vector4.Transform(new Vector4(corner, 1), matrix);
                if (clip.W > 0 && Math.Abs(clip.X) <= clip.W && Math.Abs(clip.Y) <= clip.W && clip.Z >= 0 && clip.Z <= clip.W)
                    anyCornerInside = true;
            }

            if (anyCornerInside)
                Assert.True(frustum.Intersects(min, max), $"Видимая коробка {min}..{max} отброшена");
        }
    }

    [Fact]
    public void RoughlyHalfOfSurroundingChunksAreCulled()
    {
        // Порядок величины: камера видит меньше половины окружающих её чанков.
        var visible = 0;
        var total = 0;
        for (var x = -7; x < 7; x++)
        for (var y = -3; y < 3; y++)
        for (var z = -7; z < 7; z++)
        {
            total++;
            if (Box(x * 32, y * 32, z * 32, size: 32))
                visible++;
        }

        Assert.InRange(visible, total / 10, total / 2);
    }
}
