using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;

namespace Client.Rendering;

/// <summary>
/// Часть модели моба — короб с точкой поворота. Всё в пикселях модели (16 на блок): начало координат — точка ног
/// (центр низа тела), +Y — вверх, перед моба смотрит на −Z (как камера при повороте 0).
/// </summary>
/// <param name="From">Угол короба с наименьшими координатами.</param>
/// <param name="To">Противоположный угол.</param>
/// <param name="Pivot">Точка, вокруг которой часть поворачивается (плечо, бедро, шея).</param>
/// <param name="Uv">Левый верхний угол развёртки короба на текстуре, пиксели (<see cref="BoxUv"/>).</param>
/// <param name="Rotation">Постоянный поворот вокруг осей X, Y, Z, градусы (руки зомби вытянуты вперёд — X = 90).</param>
/// <param name="Look">Голова: поворачивается вслед за взглядом моба.</param>
/// <param name="Walk">Размах качания при ходьбе вокруг X, градусы; знак — фаза: у ног 40 и −40 — шаг вразнобой.</param>
internal sealed record ModelPart(string Name, Vector3 From, Vector3 To, Vector3 Pivot, Vector2 Uv, Vector3 Rotation, bool Look, float Walk)
{
    public Vector3 Size => To - From;
}

/// <summary>
/// Модель моба из коробов (как в Minecraft): файл <c>Assets/Models/имя.json</c> у игры, <c>models/имя.json</c> у мода.
/// Нужна только клиенту — сервер знает лишь короб тела.
/// </summary>
/// <remarks>
/// Формат: <c>{"parts": [{"name", "box": [x1, y1, z1, x2, y2, z2], "uv": [u, v], "pivot": [x, y, z], "rotation": [x, y, z],
/// "look": true, "walk": 40}]}</c> — обязательны <c>name</c>, <c>box</c> и <c>uv</c>. Текстура — 64×64 (<see cref="TextureSize"/>).
/// </remarks>
internal sealed record MobModel(ImmutableArray<ModelPart> Parts)
{
    /// <summary>Сторона текстуры моба в пикселях: все текстуры мобов — слои одного массива, поэтому одного размера.</summary>
    public const int TextureSize = 64;

    public const float PixelsPerBlock = 16;

    /// <summary>Больше частей модели не бывает: каждая часть каждого моба — отдельный экземпляр отрисовки.</summary>
    public const int MaxParts = 32;

    /// <summary>
    /// Модель на случай, когда файла нет или он с ошибкой: один короб в размер тела, без анимации. Его развёртка не
    /// влезает в текстуру — не страшно: модель-заглушка рисуется шахматкой «нет текстуры», а она повторяется.
    /// </summary>
    public static MobModel Fallback(double width, double height)
    {
        var (w, h) = ((float)width * PixelsPerBlock, (float)height * PixelsPerBlock);
        return new MobModel([new ModelPart("body", new Vector3(-w / 2, 0, -w / 2), new Vector3(w / 2, h, w / 2), Vector3.Zero,
            Vector2.Zero, Vector3.Zero, false, 0)]);
    }

    /// <summary>Читает модель из JSON. Ошибка — <see cref="InvalidDataException"/> с понятным сообщением.</summary>
    public static MobModel Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"не JSON: {e.Message}", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("корень модели должен быть объектом {\"parts\": […]}.");
            CheckProperties(root, "модели", "parts");
            if (!root.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
                throw new InvalidDataException("нужен массив частей 'parts', хотя бы одна.");
            if (parts.GetArrayLength() > MaxParts)
                throw new InvalidDataException($"частей {parts.GetArrayLength()}, больше {MaxParts} нельзя.");

            var result = ImmutableArray.CreateBuilder<ModelPart>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in parts.EnumerateArray())
            {
                var part = PartOf(element);
                if (!names.Add(part.Name))
                    throw new InvalidDataException($"часть '{part.Name}' объявлена дважды.");
                result.Add(part);
            }
            return new MobModel(result.ToImmutable());
        }
    }

    private static ModelPart PartOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("каждая часть — объект {name, box, uv, …}.");
        var name = element.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(nameValue.GetString())
            ? nameValue.GetString()!
            : throw new InvalidDataException("у части нет имени 'name' (строка).");
        CheckProperties(element, $"части '{name}'", "name", "box", "uv", "pivot", "rotation", "look", "walk");

        var box = Numbers(element, "box", 6, name) ?? throw new InvalidDataException($"у части '{name}' нет короба 'box' [x1, y1, z1, x2, y2, z2].");
        var (from, to) = (new Vector3(box[0], box[1], box[2]), new Vector3(box[3], box[4], box[5]));
        if (from.X >= to.X || from.Y >= to.Y || from.Z >= to.Z)
            throw new InvalidDataException($"у короба части '{name}' начало должно быть меньше конца по каждой оси.");
        var uv = Numbers(element, "uv", 2, name) ?? throw new InvalidDataException($"у части '{name}' нет 'uv' [u, v] — угла развёртки на текстуре.");
        var origin = new Vector2(uv[0], uv[1]);
        var extent = BoxUv.Extent(to - from);
        if (origin.X < 0 || origin.Y < 0 || origin.X + extent.X > TextureSize || origin.Y + extent.Y > TextureSize)
            throw new InvalidDataException($"развёртка части '{name}' ({extent.X}×{extent.Y} от {origin.X}, {origin.Y}) " +
                                           $"не помещается в текстуру {TextureSize}×{TextureSize}.");

        var pivot = Numbers(element, "pivot", 3, name) is { } p ? new Vector3(p[0], p[1], p[2]) : Vector3.Zero;
        var rotation = Numbers(element, "rotation", 3, name) is { } r ? new Vector3(r[0], r[1], r[2]) : Vector3.Zero;
        var look = false;
        if (element.TryGetProperty("look", out var lookValue))
        {
            look = lookValue.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException($"'look' части '{name}' — true или false."),
            };
        }
        var walk = 0f;
        if (element.TryGetProperty("walk", out var walkValue))
        {
            if (walkValue.ValueKind != JsonValueKind.Number || !double.IsFinite(walkValue.GetDouble()) || Math.Abs(walkValue.GetDouble()) > 180)
                throw new InvalidDataException($"'walk' части '{name}' — число градусов от −180 до 180.");
            walk = (float)walkValue.GetDouble();
        }
        return new ModelPart(name, from, to, pivot, origin, rotation, look, walk);
    }

    /// <summary>Массив из <paramref name="count"/> конечных чисел или null, если поля нет.</summary>
    private static float[]? Numbers(JsonElement element, string property, int count, string part)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count
            || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number || !double.IsFinite(v.GetDouble())))
            throw new InvalidDataException($"'{property}' части '{part}' — массив из {count} чисел.");
        return value.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
    }

    private static void CheckProperties(JsonElement element, string what, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"неизвестное поле '{property.Name}' у {what}; допустимы {string.Join(", ", allowed)}.");
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"поле '{property.Name}' у {what} указано дважды.");
        }
    }
}

/// <summary>Грань короба модели: наружу, вправо и вверх, если смотреть на неё снаружи, и её место на текстуре.</summary>
/// <param name="Rect">Прямоугольник на текстуре, пиксели: X, Y — левый верхний угол, Z, W — ширина и высота.</param>
internal readonly record struct BoxFace(Vector3 Normal, Vector3 Right, Vector3 Up, Vector4 Rect);

/// <summary>
/// Развёртка короба на текстуре — как у моделей Minecraft. От угла <c>(u, v)</c>, при размере короба
/// <c>w × h × d</c> (X, Y, Z) пикселей:
/// <code>
///          u+d    u+d+w  u+d+2w
///     v    [ верх ][ низ  ]
///     v+d  [+X][ перед −Z ][−X][ спина +Z ]
///     v+d+h
/// </code>
/// Боковые грани идут кольцом: правый край каждой примыкает к левому краю следующей, поэтому рисунок не рвётся на углах.
/// Перед — посередине: слева от него на текстуре бок +X (правый бок моба, если смотреть ему в лицо, — слева от зрителя).
/// </summary>
internal static class BoxUv
{
    /// <summary>Сколько места на текстуре займёт развёртка короба: <c>2(w + d) × (d + h)</c>.</summary>
    public static Vector2 Extent(Vector3 size) => new(2 * (size.X + size.Z), size.Z + size.Y);

    /// <summary>Шесть граней короба размером <paramref name="size"/> с развёрткой от <paramref name="uv"/>.</summary>
    public static BoxFace[] Faces(Vector2 uv, Vector3 size)
    {
        var (w, h, d) = (size.X, size.Y, size.Z);
        var (u, v) = (uv.X, uv.Y);
        // Вправо = (взгляд на грань) × (вверх): тогда углы «левый верх → левый низ → правый низ» идут против часовой.
        return
        [
            new(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY, new Vector4(u, v + d, d, h)),
            new(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY, new Vector4(u + d, v + d, w, h)),
            new(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, new Vector4(u + d + w, v + d, d, h)),
            new(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, new Vector4(u + 2 * d + w, v + d, w, h)),
            new(Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ, new Vector4(u + d, v, w, d)),     // перед (−Z) — у нижнего края, к переду
            new(-Vector3.UnitY, -Vector3.UnitX, -Vector3.UnitZ, new Vector4(u + d + w, v, w, d)),
        ];
    }
}
