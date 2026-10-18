using Shared;
using Shared.Items;
using Shared.Voxels;

namespace Api;

/// <summary>
/// Что делает предмет при основном и дополнительном действии игрока (ЛКМ и ПКМ). Исполняется только на сервере,
/// в его потоке. Новый вид предмета (инструмент, еда, оружие) — новая реализация; протокол и клиент при этом не меняются.
/// </summary>
public interface IItemBehavior
{
    void Use(ItemAction action, in ItemUseContext context);
}

/// <summary>Всё, что поведению предмета нужно знать о применении.</summary>
/// <param name="World">Мир: менять блоки — только через него, он рассылает изменения игрокам.</param>
/// <param name="Player">Игрок, применивший предмет; предмет в руке — выбранный слот его инвентаря.</param>
/// <param name="Target">Блок под прицелом по лучу, повторённому сервером, или null, если в пределах дальности пусто.</param>
/// <param name="EyePosition">Точка глаза игрока, из которой сервер пускал луч.</param>
/// <param name="Breaker">Ломание удержанием от имени этого игрока: так ломают рука и инструменты.</param>
public readonly record struct ItemUseContext(IWorld World, ItemRegistry Items, IPlayer Player, BlockHit? Target, Vector3d EyePosition,
    IBlockBreaker Breaker)
{
    public Inventory Inventory => Player.Inventory;

    public ItemStack HeldItem => Player.Inventory.Selected;

    /// <summary>Творческий режим: ломание ничего не даёт, установка ничего не тратит.</summary>
    public bool InfiniteItems => Player.Mode == GameMode.Creative;
}
