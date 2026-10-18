using Api;
using Shared.Items;
using Shared.Voxels;

namespace Server.Items;

/// <summary>
/// Предмет-блок в руке (<c>base:place_block</c>): основное действие начинает ломать блок под прицелом (как рукой),
/// дополнительное ставит этот блок рядом. Регистрирует встроенный мод <see cref="Modding.BaseMod"/>.
/// </summary>
internal sealed class BlockItemBehavior : IItemBehavior
{
    public void Use(ItemAction action, in ItemUseContext context)
    {
        switch (action)
        {
            case ItemAction.Primary:
                if (context.Target is { } breakTarget)
                    context.Breaker.StartBreaking(breakTarget.Block);
                break;

            case ItemAction.Secondary:
                if (context.Target is { } target)
                    TryPlace(context, target);
                break;
        }
    }

    private static void TryPlace(in ItemUseContext context, BlockHit target)
    {
        // Нулевая нормаль: луч начался внутри блока, грани, к которой можно приставить блок, нет.
        if (target.Normal == default || context.HeldItem.IsEmpty || context.Items.Get(context.HeldItem.Item).Block is not { } block)
            return;

        var position = target.Block + target.Normal;
        if (!context.World.GetBlock(position).IsAir)
            return;

        // Твёрдый блок в теле игрока (своём или чужом) застрял бы в нём.
        if (context.World.IsBlockedByPlayer(position, block))
            return;

        // В выживании поставленный блок тратит одну штуку; последняя освобождает слот.
        if (context.World.TryPlaceBlock(position, block, context.Player) && !context.InfiniteItems)
            context.Inventory.Remove(context.Inventory.SelectedSlot, 1);
    }
}
