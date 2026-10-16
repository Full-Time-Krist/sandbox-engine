using System.Numerics;
using Shared;
using Shared.Blocks;
using Shared.Items;
using Shared.Voxels;

namespace Server.Items;

/// <summary>Изменение мира с рассылкой клиентам. Поведения предметов меняют мир только через него.</summary>
internal interface IBlockEditor
{
    BlockId GetBlock(BlockPos pos);

    /// <summary>Стоит ли блок <paramref name="block"/> в точке <paramref name="pos"/> поперёк тела какого-нибудь игрока.</summary>
    bool IsBlockedByPlayer(BlockPos pos, BlockId block);

    /// <summary>Меняет блок и рассылает изменение. False, если чанк не загружен.</summary>
    bool TrySetBlock(BlockPos pos, BlockId block);
}

/// <summary>Всё, что поведению предмета нужно знать о применении.</summary>
/// <param name="Inventory">Инвентарь игрока: предмет в руке — его выбранный слот, добыча кладётся сюда же.</param>
/// <param name="Mode">Режим игрока: в творческом предметы бесконечные — добыча не нужна, трата не нужна.</param>
/// <param name="Target">Блок под прицелом по лучу, повторённому сервером, или null, если в пределах дальности пусто.</param>
internal readonly record struct ItemUseContext(IBlockEditor World, ItemRegistry Items, Inventory Inventory, GameMode Mode,
    BlockHit? Target, Vector3d EyePosition)
{
    public ItemStack HeldItem => Inventory.Selected;

    /// <summary>Творческий режим: ломание ничего не даёт, установка ничего не тратит.</summary>
    public bool InfiniteItems => Mode == GameMode.Creative;
}

/// <summary>
/// Что делает предмет при основном и дополнительном действии. Новый вид предмета (инструмент, еда, оружие)
/// — новая реализация этого интерфейса; сообщения протокола и клиент при этом не меняются.
/// </summary>
internal interface IItemBehavior
{
    void Use(ItemAction action, in ItemUseContext context);
}
