using Api;
using Shared.Blocks;
using Shared.Items;

namespace ExampleMod;

/// <summary>Волшебная палочка: ЛКМ по камню превращает его в аметистовую руду. ПКМ ничего не делает.</summary>
internal sealed class WandBehavior(BlockId stone, BlockId amethystOre) : IItemBehavior
{
    public void Use(ItemAction action, in ItemUseContext context)
    {
        if (action != ItemAction.Primary || context.Target is not { } target)
            return;

        // Превращение — не ломание и не установка игроком, поэтому без событий: TrySetBlock.
        if (context.World.GetBlock(target.Block) == stone)
            context.World.TrySetBlock(target.Block, amethystOre);
    }
}
