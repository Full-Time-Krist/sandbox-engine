using Api;
using Shared.Items;

namespace Server.Items;

/// <summary>
/// Пустая рука, инструменты и предметы без своего поведения (<c>base:hand</c>): основное действие начинает ломать блок
/// под прицелом (время — по твёрдости и инструменту в руке), дополнительное ничего не делает.
/// Регистрирует встроенный мод <see cref="Modding.BaseMod"/>.
/// </summary>
internal sealed class HandBehavior : IItemBehavior
{
    public void Use(ItemAction action, in ItemUseContext context)
    {
        if (action == ItemAction.Primary && context.Target is { } target)
            context.Breaker.StartBreaking(target.Block);
    }
}
