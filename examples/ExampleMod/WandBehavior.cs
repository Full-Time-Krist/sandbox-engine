using Api;
using Shared.Blocks;
using Shared.Items;

namespace ExampleMod;

/// <summary>
/// Волшебная палочка: ЛКМ по камню превращает его в аметистовую руду (она светится — поле <c>light</c> в её JSON),
/// ПКМ пишет в лог мода свет перед гранью, в которую смотрит игрок, и время суток (<see cref="IWorld.GetLight"/>,
/// <see cref="IWorld.Time"/>).
/// </summary>
internal sealed class WandBehavior(BlockId stone, BlockId amethystOre, Action<string> log) : IItemBehavior
{
    public void Use(ItemAction action, in ItemUseContext context)
    {
        if (context.Target is not { } target)
            return;

        if (action == ItemAction.Secondary)
        {
            // Свет внутри непрозрачного блока — только его собственное свечение; интереснее воздух перед гранью.
            var pos = target.Block + target.Normal;
            var light = context.World.GetLight(pos);
            var time = context.World.Time;
            log($"{context.Player.Name}: свет в {pos} — небо {light.Sky}, блоки {light.Block}, сейчас {light.Effective(time):0.#} из 15; {time}");
            return;
        }

        // Превращение — не ломание и не установка игроком, поэтому без событий: TrySetBlock.
        if (context.World.GetBlock(target.Block) == stone)
            context.World.TrySetBlock(target.Block, amethystOre);
    }
}
