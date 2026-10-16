using Shared.Blocks;
using Shared.Items;

namespace Server.Items;

/// <summary>
/// Пустая рука и предметы без своего поведения: основное действие ломает блок под прицелом, дополнительное ничего не делает.
/// </summary>
internal sealed class HandBehavior : IItemBehavior
{
    public static readonly HandBehavior Instance = new();

    public void Use(ItemAction action, in ItemUseContext context)
    {
        if (action == ItemAction.Primary)
            BreakTarget(context);
    }

    /// <summary>
    /// Ломает блок под прицелом и в выживании кладёт его предмет в инвентарь. Если положить некуда, блок не ломается:
    /// предметов на земле пока нет (сущности — этап 4), и добыча пропала бы молча. В творческом добыча не нужна.
    /// </summary>
    public static void BreakTarget(in ItemUseContext context)
    {
        if (context.Target is not { } target)
            return;

        if (context.InfiniteItems)
        {
            context.World.TrySetBlock(target.Block, BlockId.Air);
            return;
        }

        var drop = new ItemStack(context.Items.ForBlock(context.World.GetBlock(target.Block)), 1);
        if (!context.Inventory.CanAdd(drop))
            return;

        if (context.World.TrySetBlock(target.Block, BlockId.Air))
            context.Inventory.Add(drop);
    }
}
