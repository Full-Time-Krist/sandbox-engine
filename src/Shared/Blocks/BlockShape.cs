using System.Collections.Immutable;
using System.Numerics;

namespace Shared.Blocks;

/// <summary>Короб формы блока — в долях блока, внутри [0; 1]³ (из JSON — в пикселях 0–16).</summary>
public readonly record struct ShapeBox(Vector3 Min, Vector3 Max)
{
    /// <summary>Короб из пикселей 0–16: <c>[x1, y1, z1, x2, y2, z2]</c>.</summary>
    public static ShapeBox FromPixels(float x1, float y1, float z1, float x2, float y2, float z2) =>
        new(new Vector3(x1, y1, z1) / BlockShape.Pixels, new Vector3(x2, y2, z2) / BlockShape.Pixels);

    public Vector3 Size => Max - Min;
}

/// <summary>
/// Форма блока — один или несколько коробов внутри его клетки (поле <c>model</c> или <c>shape</c> в JSON). По одним и тем
/// же коробам блок рисуется, в него целится луч, вокруг них — рамка выделения, о них ударяются тела (если блок твёрдый).
/// </summary>
/// <remarks>
/// Короба не выходят за клетку: столкновения и луч ищут блоки по клеткам, и короб снаружи нашёлся бы не всегда.
/// Готовые формы — <see cref="Cube"/>, <see cref="Torch"/>, <see cref="Slab"/>; мод может описать свои короба.
/// </remarks>
public sealed class BlockShape
{
    /// <summary>Пикселей в стороне блока: в них задаются короба в JSON.</summary>
    public const float Pixels = 16;

    /// <summary>Полный куб — по умолчанию.</summary>
    public static readonly BlockShape Cube = new("cube", [new ShapeBox(Vector3.Zero, Vector3.One)]);

    /// <summary>Факел: столбик 2 × 10 × 2 пикселя в центре, стоит на полу.</summary>
    public static readonly BlockShape Torch = new("torch", [ShapeBox.FromPixels(7, 0, 7, 9, 10, 9)]);

    /// <summary>Нижний полублок: нижняя половина клетки.</summary>
    public static readonly BlockShape Slab = new("slab", [ShapeBox.FromPixels(0, 0, 0, 16, 8, 16)]);

    /// <summary>Готовые формы по имени (поле <c>model</c>).</summary>
    public static readonly ImmutableArray<BlockShape> Named = [Cube, Torch, Slab];

    /// <param name="name">Имя готовой формы или null для своей (поле <c>shape</c>).</param>
    public BlockShape(string? name, ImmutableArray<ShapeBox> boxes)
    {
        if (boxes.IsDefaultOrEmpty)
            throw new ArgumentException("У формы блока должен быть хотя бы один короб.", nameof(boxes));
        foreach (var box in boxes)
        {
            if (!(Vector3.Min(box.Min, Vector3.Zero) == Vector3.Zero && Vector3.Max(box.Max, Vector3.One) == Vector3.One
                  && box.Min.X < box.Max.X && box.Min.Y < box.Max.Y && box.Min.Z < box.Max.Z))
                throw new ArgumentException($"Короб формы {box} должен лежать внутри блока и иметь объём.", nameof(boxes));
        }

        Name = name;
        Boxes = boxes;
        IsFullCube = boxes.Any(box => box.Min == Vector3.Zero && box.Max == Vector3.One);
        HasFullTop = boxes.Any(box => box.Max.Y == 1 && box.Min.X == 0 && box.Max.X == 1 && box.Min.Z == 0 && box.Max.Z == 1);
        var min = boxes[0].Min;
        var max = boxes[0].Max;
        foreach (var box in boxes)
        {
            min = Vector3.Min(min, box.Min);
            max = Vector3.Max(max, box.Max);
        }
        Bounds = new ShapeBox(min, max);
    }

    /// <summary>Имя готовой формы (<c>cube</c>, <c>torch</c>, <c>slab</c>) или null у своей.</summary>
    public string? Name { get; }

    public ImmutableArray<ShapeBox> Boxes { get; }

    /// <summary>Заполняет клетку целиком: тогда хватает куба, и луч, столкновения и мешер идут по быстрому пути.</summary>
    public bool IsFullCube { get; }

    /// <summary>
    /// Верх клетки закрыт коробом целиком: на такой блок можно поставить блок с опорой. На нижнюю плиту — нельзя: факел
    /// стоит на дне своей клетки и висел бы на полблока выше её верха.
    /// </summary>
    public bool HasFullTop { get; }

    /// <summary>Общий короб всех коробов: по нему рисуются иконка, предмет на земле и трещины.</summary>
    public ShapeBox Bounds { get; }

    public override string ToString() => Name ?? $"[{string.Join(", ", Boxes)}]";
}
