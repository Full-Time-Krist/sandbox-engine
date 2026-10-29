using System.Numerics;
using Client.Rendering;
using Engine.Graphics;
using Shared.Content;

namespace Client.Tests.Rendering;

/// <summary>Модели мобов (шаг 9.2): JSON модели, развёртка текстуры, меш частей и поза.</summary>
public class MobModelTests
{
    private const string Leg = """{"name": "leg", "box": [0, 0, -2, 4, 12, 2], "pivot": [2, 12, 0], "uv": [0, 16], "walk": 40}""";

    private static MobModel Parse(params string[] parts) => MobModel.Parse($$"""{"parts": [{{string.Join(", ", parts)}}]}""");

    [Fact]
    public void Parse_ReadsParts_WithDefaults()
    {
        var model = Parse(Leg, """{"name": "head", "box": [-4, 24, -4, 4, 32, 4], "uv": [0, 0], "look": true, "rotation": [10, 0, -5]}""");

        var leg = model.Parts[0];
        Assert.Equal(("leg", new Vector3(0, 0, -2), new Vector3(4, 12, 2)), (leg.Name, leg.From, leg.To));
        Assert.Equal((new Vector3(2, 12, 0), new Vector2(0, 16), 40f, false), (leg.Pivot, leg.Uv, leg.Walk, leg.Look));
        Assert.Equal(Vector3.Zero, leg.Rotation);
        var head = model.Parts[1];
        Assert.Equal((Vector3.Zero, true, 0f, new Vector3(10, 0, -5)), (head.Pivot, head.Look, head.Walk, head.Rotation));
    }

    [Theory]
    [InlineData("""{"parts": []}""", "хотя бы одна")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 1, 1, 1], "uv": [0, 0]}], "scale": 2}""", "'scale'")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 1, 1, 1], "uv": [0, 0], "color": 1}]}""", "'color'")]
    [InlineData("""{"parts": [{"name": "a", "uv": [0, 0]}]}""", "'box'")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 1, 1], "uv": [0, 0]}]}""", "'box'")]
    [InlineData("""{"parts": [{"name": "a", "box": [2, 0, 0, 1, 1, 1], "uv": [0, 0]}]}""", "меньше конца")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 1, 1, 1]}]}""", "'uv'")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 8, 8, 8], "uv": [40, 0]}]}""", "не помещается")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 8, 8, 8], "uv": [0, 50]}]}""", "не помещается")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 1, 1, 1], "uv": [-1, 0]}]}""", "не помещается")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 1, 1, 1], "uv": [0, 0]}, {"name": "a", "box": [0, 0, 0, 1, 1, 1], "uv": [0, 0]}]}""", "дважды")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 1, 1, 1], "uv": [0, 0], "walk": "40"}]}""", "'walk'")]
    [InlineData("""{"parts": [{"name": "a", "box": [0, 0, 0, 1, 1, 1], "uv": [0, 0], "look": 1}]}""", "'look'")]
    [InlineData("""{"parts": [{"box": [0, 0, 0, 1, 1, 1], "uv": [0, 0]}]}""", "'name'")]
    [InlineData("""{"parts": [""", "не JSON")]
    public void Parse_Errors_SayWhatIsWrong(string json, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => MobModel.Parse(json));
        Assert.Contains(expected, error.Message);
    }

    /// <summary>Развёртка как у Minecraft: верх и низ сверху, под ними кольцом +X, перед (−Z), −X, спина.</summary>
    [Fact]
    public void BoxUv_LaysOutFaces_LikeMinecraft()
    {
        var faces = BoxUv.Faces(new Vector2(16, 16), new Vector3(8, 12, 4)); // тело гуманоида: w 8, h 12, d 4
        Vector4 RectOf(Vector3 normal) => faces.Single(f => f.Normal == normal).Rect;

        Assert.Equal(new Vector4(16, 20, 4, 12), RectOf(Vector3.UnitX));
        Assert.Equal(new Vector4(20, 20, 8, 12), RectOf(-Vector3.UnitZ));
        Assert.Equal(new Vector4(28, 20, 4, 12), RectOf(-Vector3.UnitX));
        Assert.Equal(new Vector4(32, 20, 8, 12), RectOf(Vector3.UnitZ));
        Assert.Equal(new Vector4(20, 16, 8, 4), RectOf(Vector3.UnitY));
        Assert.Equal(new Vector4(28, 16, 8, 4), RectOf(-Vector3.UnitY));
        Assert.Equal(new Vector2(24, 16), BoxUv.Extent(new Vector3(8, 12, 4)));
    }

    private static readonly ModelPart Body = MobModel.Parse(
        """{"parts": [{"name": "body", "box": [-4, 12, -2, 4, 24, 2], "pivot": [1, 2, 3], "uv": [16, 16]}]}""").Parts[0];

    /// <summary>Треугольники против часовой стрелки снаружи (задние отсекаются — иначе моб был бы вывернут) и лежат на коробе.</summary>
    [Fact]
    public void Mesh_IsOutwardFacing_AndOnTheBox()
    {
        var mesh = MobPose.BuildMesh(Body);
        Assert.Equal(36, mesh.Length);
        for (var i = 0; i < mesh.Length; i += 3)
        {
            var (a, b, c) = (mesh[i].Position, mesh[i + 1].Position, mesh[i + 2].Position);
            Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), mesh[i].Normal) > 0, $"треугольник {i / 3} вывернут");
        }

        // Позиции — от точки поворота, в блоках: углы короба, сдвинутые на −pivot и делённые на 16.
        var min = (Body.From - Body.Pivot) / 16;
        var max = (Body.To - Body.Pivot) / 16;
        Assert.All(mesh, v => Assert.True(
            v.Position.X is var x && (Near(x, min.X) || Near(x, max.X)) && (Near(v.Position.Y, min.Y) || Near(v.Position.Y, max.Y))
            && (Near(v.Position.Z, min.Z) || Near(v.Position.Z, max.Z)), $"{v.Position} не угол короба"));
    }

    /// <summary>
    /// UV углов — по развёртке, и рисунок не рвётся на рёбрах: у угла, общего для двух соседних боковых граней кольца,
    /// одни и те же UV (правый край одной грани примыкает к левому краю следующей).
    /// </summary>
    [Fact]
    public void Mesh_Uv_FollowsLayout_AndSideRingIsSeamless()
    {
        var mesh = MobPose.BuildMesh(Body);
        Vector2 UvAt(Vector3 normal, Vector3 pixel) =>
            mesh.First(v => v.Normal == normal && Vector3.Distance(v.Position, (pixel - Body.Pivot) / 16) < 1e-5f).Uv * 64;

        // Перед (−Z): левый верхний угол на текстуре — у бока +X сверху (смотрим мобу в лицо — его правый бок слева).
        Assert.Equal(new Vector2(20, 20), UvAt(-Vector3.UnitZ, new Vector3(4, 24, -2)));
        Assert.Equal(new Vector2(28, 32), UvAt(-Vector3.UnitZ, new Vector3(-4, 12, -2)));

        // Швы кольца: +X → перед → −X → спина.
        Assert.Equal(UvAt(Vector3.UnitX, new Vector3(4, 24, -2)), UvAt(-Vector3.UnitZ, new Vector3(4, 24, -2)));
        Assert.Equal(UvAt(-Vector3.UnitZ, new Vector3(-4, 12, -2)), UvAt(-Vector3.UnitX, new Vector3(-4, 12, -2)));
        Assert.Equal(UvAt(-Vector3.UnitX, new Vector3(-4, 24, 2)), UvAt(Vector3.UnitZ, new Vector3(-4, 24, 2)));
        // Верх прилегает к переду: передний край верха — над верхним краем переда.
        Assert.Equal(UvAt(-Vector3.UnitZ, new Vector3(4, 24, -2)), UvAt(Vector3.UnitY, new Vector3(4, 24, -2)));
        Assert.Equal(UvAt(-Vector3.UnitZ, new Vector3(-4, 24, -2)), UvAt(Vector3.UnitY, new Vector3(-4, 24, -2)));
    }

    private static readonly MobModel Humanoid = Parse(
        """{"name": "head", "box": [-4, 24, -4, 4, 32, 4], "pivot": [0, 24, 0], "uv": [0, 0], "look": true}""",
        """{"name": "right_leg", "box": [0, 0, -2, 4, 12, 2], "pivot": [2, 12, 0], "uv": [0, 16], "walk": 40}""",
        """{"name": "left_leg", "box": [-4, 0, -2, 0, 12, 2], "pivot": [-2, 12, 0], "uv": [0, 16], "walk": -40}""",
        """{"name": "arm", "box": [4, 12, -2, 8, 24, 2], "pivot": [6, 22, 0], "uv": [40, 16], "rotation": [90, 0, 0]}""");

    private static Matrix4x4[] Pose(float yaw = 0, float headTurn = 0, float walk = 0, float swing = 0)
    {
        var result = new Matrix4x4[Humanoid.Parts.Length];
        MobPose.Compute(Humanoid, new MobPoseState(yaw, headTurn, walk, swing), result);
        return result;
    }

    /// <summary>Куда после позы смотрит направление <paramref name="local"/> части (без переноса).</summary>
    private static Vector3 Direction(Matrix4x4 pose, Vector3 local) => Vector3.TransformNormal(local, pose);

    /// <summary>Стоя: части на своих местах — точка поворота в своей точке модели, ноги смотрят вниз, рука — вперёд (rotation).</summary>
    [Fact]
    public void Pose_AtRest_PartsInPlace()
    {
        var pose = Pose(walk: 0.37f, swing: 0);
        Assert.Equal(new Vector3(2, 12, 0) / 16, Vector3.Transform(Vector3.Zero, pose[1]));
        AssertNear(-Vector3.UnitY, Direction(pose[1], -Vector3.UnitY));
        AssertNear(-Vector3.UnitY, Direction(pose[2], -Vector3.UnitY));
        AssertNear(-Vector3.UnitZ, Direction(pose[3], -Vector3.UnitY)); // рука висела вниз, rotation 90 вокруг X — вперёд
    }

    /// <summary>На ходу ноги качаются вразнобой (walk 40 и −40) по фазе пройденного пути; размах — по скорости.</summary>
    [Fact]
    public void Pose_Walking_LegsSwingOpposite_ByWalkPhase()
    {
        // Четверть шага: синус фазы = 1, отклонение — весь размах 40°.
        var quarter = MathF.PI / 2 / MobPose.StridePerBlock;
        var pose = Pose(walk: quarter, swing: 1);
        var right = Direction(pose[1], -Vector3.UnitY);
        var left = Direction(pose[2], -Vector3.UnitY);
        var swing = 40 * MathF.PI / 180;
        AssertNear(new Vector3(0, -MathF.Cos(swing), -MathF.Sin(swing)), right); // правая — вперёд (−Z)
        AssertNear(new Vector3(0, -MathF.Cos(swing), MathF.Sin(swing)), left);   // левая — назад
        AssertNear(Direction(pose[0], -Vector3.UnitZ), -Vector3.UnitZ);          // голова от шага не качается

        // Половина размаха — половина угла; полшага — ноги снова вертикальны.
        AssertNear(new Vector3(0, -MathF.Cos(swing / 2), -MathF.Sin(swing / 2)), Direction(Pose(walk: quarter, swing: 0.5f)[1], -Vector3.UnitY));
        AssertNear(-Vector3.UnitY, Direction(Pose(walk: 2 * quarter, swing: 1)[1], -Vector3.UnitY));
    }

    /// <summary>Поворот тела — как у камеры: yaw π/2 — перед (−Z) смотрит на +X; голова сверх того — на поворот головы.</summary>
    [Fact]
    public void Pose_BodyYaw_AndHeadTurn()
    {
        var pose = Pose(yaw: MathF.PI / 2);
        AssertNear(Vector3.UnitX, Direction(pose[1], -Vector3.UnitZ));
        AssertNear(Vector3.UnitX, Direction(pose[0], -Vector3.UnitZ));
        // Точка поворота ноги (2, 12, 0) уходит вместе с телом: +X модели после поворота — на +Z.
        AssertNear(new Vector3(0, 12, 2) / 16, Vector3.Transform(Vector3.Zero, pose[1]));

        var turned = Pose(yaw: MathF.PI / 2, headTurn: -MathF.PI / 2);
        AssertNear(-Vector3.UnitZ, Direction(turned[0], -Vector3.UnitZ)); // тело на +X, голова повернулась обратно на −Z
        AssertNear(Vector3.UnitX, Direction(turned[1], -Vector3.UnitZ));  // а ноги — нет
        AssertNear(new Vector3(0, 24, 0) / 16, Vector3.Transform(Vector3.Zero, turned[0])); // шея на месте
    }

    [Fact]
    public void Flash_FullAfterHurt_FadesOut()
    {
        Assert.Equal(1f, MobPose.Flash(0));
        Assert.Equal(0.5f, MobPose.Flash(MobPose.FlashSeconds / 2), 4);
        Assert.Equal(0f, MobPose.Flash(MobPose.FlashSeconds));
        Assert.Equal(0f, MobPose.Flash(double.PositiveInfinity)); // не получал урона (HurtAt = −∞)
        Assert.Equal(0f, MobPose.Flash(-1));
    }

    /// <summary>Модели и текстуры ванильных мобов на месте, читаются без ошибок, текстуры — 64×64.</summary>
    [Fact]
    public void VanillaMobs_HaveModelsAndTextures()
    {
        var content = GameContent.Load();
        var log = new List<string>();
        var files = new TextureFiles([]);
        var visuals = new MobVisuals(content.Mobs, content.MobLooks, files, log.Add);

        Assert.Empty(log);
        Assert.Equal(["", "base:sheep", "base:zombie"], visuals.TextureNames);
        foreach (var mob in content.Mobs.All)
        {
            Assert.True(visuals.Layer(mob.Id) > 0, mob.Name);
            Assert.True(visuals.Model(mob.Id).Parts.Any(p => p.Look), $"{mob.Name}: нет головы");
            Assert.True(visuals.Model(mob.Id).Parts.Count(p => p.Walk != 0) >= 2, $"{mob.Name}: нет ног");
            var image = Image.Load(files.PathFor(content.MobLooks[mob.Name].Texture, TextureKind.Mob));
            Assert.Equal((MobModel.TextureSize, MobModel.TextureSize), (image.Width, image.Height));
        }
    }

    /// <summary>
    /// Нет файла модели или он с ошибкой — моб не пропадает и игра не падает: в лог — причина, вид — короб в размер тела
    /// шахматкой «нет текстуры» (слой 0). Общая модель двух мобов читается один раз, общая текстура — один слой.
    /// </summary>
    [Fact]
    public void MissingOrBrokenModel_FallsBackToBodyBox_WithLog()
    {
        var directory = Directory.CreateTempSubdirectory("mob-models-").FullName;
        try
        {
            var modelDirectory = Path.Combine(directory, "models");
            Directory.CreateDirectory(modelDirectory);
            File.WriteAllText(Path.Combine(modelDirectory, "broken.json"), """{"parts": [{"name": "a"}]}""");
            File.WriteAllText(Path.Combine(modelDirectory, "good.json"), $$"""{"parts": [{{Leg}}]}""");
            var mod = new Shared.Modding.ModInfo("m", "M", new Version(1, 0), new Dictionary<string, Version>(), directory, null);
            var content = ContentLoader.Load([
                new ContentJsonFile("m/a.json", """{"type":"mob","name":"m:a","health":1,"speed":1,"size":{"width":0.5,"height":1.5},"behavior":"m:x","model":"broken"}"""),
                new ContentJsonFile("m/b.json", """{"type":"mob","name":"m:b","health":1,"speed":1,"size":{"width":1,"height":1},"behavior":"m:x"}"""),
                new ContentJsonFile("m/c.json", """{"type":"mob","name":"m:c","health":1,"speed":1,"size":{"width":1,"height":1},"behavior":"m:x","model":"good","texture":"skin"}"""),
                new ContentJsonFile("m/d.json", """{"type":"mob","name":"m:d","health":1,"speed":1,"size":{"width":1,"height":1},"behavior":"m:x","model":"good","texture":"skin"}"""),
            ]);
            var log = new List<string>();
            var visuals = new MobVisuals(content.Mobs, content.MobLooks, new TextureFiles([mod]), log.Add);

            var broken = content.Mobs.Get("m:a");
            Assert.Equal(0, visuals.Layer(broken.Id));
            var box = Assert.Single(visuals.Model(broken.Id).Parts);
            Assert.Equal((new Vector3(-4, 0, -4), new Vector3(4, 24, 4)), (box.From, box.To));
            Assert.Equal(0, visuals.Layer(content.Mobs.Get("m:b").Id)); // m:b — модели нет вовсе
            Assert.Equal(2, log.Count);
            Assert.Contains(log, line => line.Contains("m:broken") && line.Contains("'box'"));
            Assert.Contains(log, line => line.Contains("Нет модели моба m:b"));

            var (c, d) = (content.Mobs.Get("m:c").Id, content.Mobs.Get("m:d").Id);
            Assert.Same(visuals.Model(c), visuals.Model(d));
            Assert.Equal((1, 1), (visuals.Layer(c), visuals.Layer(d)));
            Assert.Equal(["", "m:skin"], visuals.TextureNames);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-5f;

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"ожидалось {expected}, получено {actual}");
}
