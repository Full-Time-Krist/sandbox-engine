using System.Numerics;
using System.Runtime.InteropServices;

namespace Client.Rendering;

/// <summary>Вершина меша части моба, 32 байта: позиция от точки поворота части (в блоках), UV текстуры и нормаль.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct MobVertex(Vector3 position, Vector2 uv, Vector3 normal)
{
    public readonly Vector3 Position = position;
    public readonly Vector2 Uv = uv;
    public readonly Vector3 Normal = normal;
}

/// <summary>Что нужно позе моба в кадре — всё уже между тиками (<see cref="ClientEntity"/>).</summary>
/// <param name="Yaw">Поворот тела, радианы, как у камеры (0 — на −Z).</param>
/// <param name="HeadTurn">Поворот головы относительно тела, радианы.</param>
/// <param name="Walk">Пройденный путь, блоков — фаза шага.</param>
/// <param name="Swing">Размах шага, 0–1.</param>
internal readonly record struct MobPoseState(float Yaw, float HeadTurn, float Walk, float Swing);

/// <summary>Меш частей модели и их поза в кадре. Без OpenGL — проверяется тестами.</summary>
internal static class MobPose
{
    /// <summary>Сколько радиан фазы шага на блок пути: полный шаг (обе ноги) — за 1,5 блока.</summary>
    public const float StridePerBlock = MathF.Tau / 1.5f;

    /// <summary>
    /// Меш одной части: 6 граней по 2 треугольника, против часовой стрелки снаружи. Позиции — от точки поворота части,
    /// в блоках (её переносит и поворачивает матрица экземпляра), UV — по развёртке <see cref="BoxUv"/>, в долях текстуры.
    /// </summary>
    public static MobVertex[] BuildMesh(ModelPart part)
    {
        var vertices = new MobVertex[36];
        var center = (part.From + part.To) / 2;
        var half = part.Size / 2;
        var i = 0;
        foreach (var face in BoxUv.Faces(part.Uv, part.Size))
        {
            Vector3 Along(Vector3 axis) => axis * MathF.Abs(Vector3.Dot(half, axis));
            var (n, r, u) = (Along(face.Normal), Along(face.Right), Along(face.Up));
            var rect = face.Rect;
            // Углы грани: левый верх, левый низ, правый низ, правый верх — и их место на текстуре (v = 0 — верх картинки).
            MobVertex V(float right, float up, float x, float y) => new(
                (center + n + r * right + u * up - part.Pivot) / MobModel.PixelsPerBlock,
                new Vector2(x, y) / MobModel.TextureSize, face.Normal);
            var topLeft = V(-1, 1, rect.X, rect.Y);
            var bottomLeft = V(-1, -1, rect.X, rect.Y + rect.W);
            var bottomRight = V(1, -1, rect.X + rect.Z, rect.Y + rect.W);
            var topRight = V(1, 1, rect.X + rect.Z, rect.Y);
            vertices[i++] = topLeft;
            vertices[i++] = bottomLeft;
            vertices[i++] = bottomRight;
            vertices[i++] = topLeft;
            vertices[i++] = bottomRight;
            vertices[i++] = topRight;
        }
        return vertices;
    }

    /// <summary>
    /// Матрицы частей в кадре (System.Numerics, порядок «вершина × матрица»): поворот части вокруг её точки — постоянный,
    /// качание при ходьбе (X), взгляд головы (Y); затем поворот всего тела. Результат — от точки ног моба, в блоках;
    /// смещение от глаза добавляет отрисовка.
    /// </summary>
    public static void Compute(MobModel model, in MobPoseState state, Span<Matrix4x4> result)
    {
        // Как у камеры: поворот yaw смотрит на (sin yaw, −cos yaw), а CreateRotationY(−yaw) переводит −Z именно туда.
        var body = Matrix4x4.CreateRotationY(-state.Yaw);
        var phase = MathF.Sin(state.Walk * StridePerBlock) * state.Swing;
        for (var i = 0; i < model.Parts.Length; i++)
        {
            var part = model.Parts[i];
            var rotation = part.Rotation * (MathF.PI / 180);
            rotation.X += part.Walk * (MathF.PI / 180) * phase;
            if (part.Look)
                rotation.Y -= state.HeadTurn;
            result[i] = Matrix4x4.CreateRotationX(rotation.X) * Matrix4x4.CreateRotationY(rotation.Y) * Matrix4x4.CreateRotationZ(rotation.Z)
                        * Matrix4x4.CreateTranslation(part.Pivot / MobModel.PixelsPerBlock) * body;
        }
    }

    /// <summary>Сколько длится красная вспышка после удара, секунды.</summary>
    public const double FlashSeconds = 0.4;

    /// <summary>Сила вспышки, 0–1: сразу после удара — полная, к концу <see cref="FlashSeconds"/> гаснет.</summary>
    public static float Flash(double secondsSinceHurt) =>
        secondsSinceHurt is >= 0 and < FlashSeconds ? (float)(1 - secondsSinceHurt / FlashSeconds) : 0f;
}
