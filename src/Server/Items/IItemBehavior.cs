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

    /// <summary>Меняет блок и рассылает изменение. False, если чанк не загружен.</summary>
    bool TrySetBlock(BlockPos pos, BlockId block);
}

/// <summary>Всё, что поведению предмета нужно знать о применении.</summary>
/// <param name="HeldItem">Предмет в руке. Пока это блок; позже — стопка предметов.</param>
/// <param name="Target">Блок под прицелом по лучу, повторённому сервером, или null, если в пределах дальности пусто.</param>
internal readonly record struct ItemUseContext(IBlockEditor World, BlockId HeldItem, BlockHit? Target, Vector3d EyePosition);

/// <summary>
/// Что делает предмет при основном и дополнительном действии. Новый вид предмета (инструмент, еда, оружие)
/// — новая реализация этого интерфейса; сообщения протокола и клиент при этом не меняются.
/// </summary>
internal interface IItemBehavior
{
    void Use(ItemAction action, in ItemUseContext context);
}
