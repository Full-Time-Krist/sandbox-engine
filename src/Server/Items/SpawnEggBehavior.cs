using Api;
using Shared;
using Shared.Items;

namespace Server.Items;

/// <summary>
/// Яйцо призыва (<c>base:spawn_mob</c>, у предмета с <c>spawnsMob</c>): дополнительное действие ставит моба на грань
/// под прицелом — в соседнюю клетку, по центру; основное ломает блок, как рука. В выживании яйцо тратится.
/// Регистрирует встроенный мод <see cref="Modding.BaseMod"/>.
/// </summary>
internal sealed class SpawnEggBehavior : IItemBehavior
{
    public void Use(ItemAction action, in ItemUseContext context)
    {
        if (context.Target is not { } target)
            return;
        if (action == ItemAction.Primary)
        {
            context.Breaker.StartBreaking(target.Block);
            return;
        }

        // Нулевая нормаль: луч начался внутри блока, приставить моба не к чему.
        if (target.Normal == default || context.HeldItem.IsEmpty)
            return;
        var mob = context.Items.Get(context.HeldItem.Item).SpawnsMob;
        var cell = target.Block + target.Normal;
        var feet = new Vector3d(cell.X + 0.5, cell.Y, cell.Z + 0.5);
        if (!mob.IsNone && context.World.TrySpawnMob(mob, feet) && !context.InfiniteItems)
            context.Inventory.Remove(context.Inventory.SelectedSlot, 1);
    }
}
