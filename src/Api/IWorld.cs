using Shared;
using Shared.Blocks;
using Shared.Items;
using Shared.Voxels;

namespace Api;

/// <summary>Мир сервера для модов. Изменения сразу рассылаются игрокам, у которых есть этот чанк.</summary>
public interface IWorld
{
    /// <summary>Блок в точке; в незагруженном чанке — воздух.</summary>
    BlockId GetBlock(BlockPos pos);

    /// <summary>Свет в точке (с шага 8.7): небесный и блочный. В незагруженном чанке — темно (0, 0).</summary>
    LightLevel GetLight(BlockPos pos);

    /// <summary>Время мира (с шага 8.7): тик мира, доля суток, день или ночь, яркость неба.</summary>
    WorldTime Time { get; }

    /// <summary>Стоит ли блок <paramref name="block"/> в точке <paramref name="pos"/> поперёк тела какого-нибудь игрока.</summary>
    bool IsBlockedByPlayer(BlockPos pos, BlockId block);

    /// <summary>
    /// Меняет блок и рассылает изменение, без событий: для правок мира, которые не действие игрока.
    /// Если блок перестал держать опору (не твёрдый или без полного верха, как плита), стоявший на нём блок с опорой
    /// (факел, <c>needsSupport</c>) ломается с добычей.
    /// Опору самого блока не проверяет: так можно поставить и факел в воздухе. False, если чанк не загружен.
    /// </summary>
    bool TrySetBlock(BlockPos pos, BlockId block);

    /// <summary>
    /// Игрок ломает блок: на его месте становится воздух, моды получают <see cref="BlockBroken"/>. Добычу это не выдаёт —
    /// это дело поведения предмета; блок с опорой над ним ломается с добычей, как у <see cref="TrySetBlock"/>.
    /// False, если там воздух или чанк не загружен.
    /// </summary>
    bool TryBreakBlock(BlockPos pos, IPlayer player);

    /// <summary>
    /// Игрок ставит блок: моды получают <see cref="BlockPlaced"/>. False, если чанк не загружен или блок с опорой
    /// (<c>needsSupport</c>) ставят не на твёрдый блок с полным верхом.
    /// </summary>
    bool TryPlaceBlock(BlockPos pos, BlockId block, IPlayer player);

    /// <summary>
    /// Ставит моба вида <paramref name="mob"/> ногами в <paramref name="feet"/> (с шага 9.1). False, если вида нет, чанк
    /// не загружен или тело во что-то упирается.
    /// </summary>
    bool TrySpawnMob(Shared.Mobs.MobId mob, Vector3d feet);

    /// <summary>
    /// Кладёт стопку предметом на землю: точка — низ предмета, он падает и ложится, как добыча; подобрать его можно через
    /// 0,5 с. Пустая стопка — ничего. В незагруженном месте предмет стоит, пока туда не придёт игрок.
    /// </summary>
    void SpawnItem(Vector3d position, ItemStack stack);
}
