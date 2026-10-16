namespace Shared.Items;

/// <summary>
/// Числовой идентификатор предмета. Раздаётся реестром при запуске и действует только в текущем сеансе.
/// Постоянное имя предмета — строковое (<see cref="ItemDefinition.Name"/>).
/// </summary>
/// <remarks>
/// Id 0 — «воздух», то есть «ничего»: пустой слот инвентаря (<see cref="ItemStack.Empty"/>), как блок-воздух в чанке.
/// </remarks>
public readonly record struct ItemId(ushort Value)
{
    public static readonly ItemId Air = new(0);

    public bool IsAir => Value == 0;

    public override string ToString() => $"#{Value}";
}
