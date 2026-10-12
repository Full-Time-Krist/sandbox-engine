namespace Shared.Blocks;

/// <summary>
/// Числовой идентификатор блока. Раздаётся реестром при запуске и действует только в текущем сеансе.
/// Постоянное имя блока — строковое (<see cref="BlockDefinition.Name"/>).
/// </summary>
/// <remarks>Занимает 2 байта, как <see cref="ushort"/>: массив блоков чанка хранит его напрямую.</remarks>
public readonly record struct BlockId(ushort Value)
{
    public static readonly BlockId Air = new(0);

    public bool IsAir => Value == 0;

    public override string ToString() => $"#{Value}";
}
