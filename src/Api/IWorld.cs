using Shared.Blocks;
using Shared.Voxels;

namespace Api;

/// <summary>Мир сервера для модов. Изменения сразу рассылаются игрокам, у которых есть этот чанк.</summary>
public interface IWorld
{
    /// <summary>Блок в точке; в незагруженном чанке — воздух.</summary>
    BlockId GetBlock(BlockPos pos);

    /// <summary>Стоит ли блок <paramref name="block"/> в точке <paramref name="pos"/> поперёк тела какого-нибудь игрока.</summary>
    bool IsBlockedByPlayer(BlockPos pos, BlockId block);

    /// <summary>Меняет блок и рассылает изменение. False, если чанк не загружен.</summary>
    bool TrySetBlock(BlockPos pos, BlockId block);
}
