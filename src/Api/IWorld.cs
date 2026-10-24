using Shared;
using Shared.Items;
using Shared.Blocks;
using Shared.Voxels;

namespace Api;

/// <summary>Мир сервера для модов. Изменения сразу рассылаются игрокам, у которых есть этот чанк.</summary>
public interface IWorld
{
    /// <summary>Создаёт предмет в загруженном чанке. Неверная стопка или позиция отклоняется.</summary>
    void SpawnItem(Vector3d position, ItemStack stack);

    /// <summary>Блок в точке; в незагруженном чанке — воздух.</summary>
    BlockId GetBlock(BlockPos pos);

    /// <summary>Стоит ли блок <paramref name="block"/> в точке <paramref name="pos"/> поперёк тела какого-нибудь игрока.</summary>
    bool IsBlockedByPlayer(BlockPos pos, BlockId block);

    /// <summary>
    /// Меняет блок и рассылает изменение, без событий: для правок мира, которые не действие игрока.
    /// False, если чанк не загружен.
    /// </summary>
    bool TrySetBlock(BlockPos pos, BlockId block);

    /// <summary>
    /// Игрок ломает блок: на его месте становится воздух, моды получают <see cref="BlockBroken"/>. Добычу это не выдаёт —
    /// это дело поведения предмета. False, если там воздух или чанк не загружен.
    /// </summary>
    bool TryBreakBlock(BlockPos pos, IPlayer player);

    /// <summary>Игрок ставит блок: моды получают <see cref="BlockPlaced"/>. False, если чанк не загружен.</summary>
    bool TryPlaceBlock(BlockPos pos, BlockId block, IPlayer player);
}
