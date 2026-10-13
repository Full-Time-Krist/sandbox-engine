using Shared.Blocks;
using Shared.Items;
using Shared.Voxels;

namespace Server.Items;

/// <summary>Поведение блока в руке: основное действие ломает блок под прицелом, дополнительное ставит блок рядом.</summary>
internal sealed class BlockItemBehavior : IItemBehavior
{
    public static readonly BlockItemBehavior Instance = new();

    public void Use(ItemAction action, in ItemUseContext context)
    {
        if (context.Target is not { } target)
            return;

        switch (action)
        {
            case ItemAction.Primary:
                context.World.TrySetBlock(target.Block, BlockId.Air);
                break;

            case ItemAction.Secondary:
                TryPlace(context, target);
                break;
        }
    }

    private static void TryPlace(in ItemUseContext context, BlockHit target)
    {
        // Нулевая нормаль: луч начался внутри блока, грани, к которой можно приставить блок, нет.
        if (target.Normal == default || context.HeldItem.IsAir)
            return;

        var position = target.Block + target.Normal;
        if (!context.World.GetBlock(position).IsAir)
            return;

        // Твёрдый блок в теле игрока (своём или чужом) застрял бы в нём.
        if (context.World.IsBlockedByPlayer(position, context.HeldItem))
            return;

        context.World.TrySetBlock(position, context.HeldItem);
    }
}
