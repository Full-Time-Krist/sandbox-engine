using Shared.Blocks;
using Shared.Items;
using Shared.Voxels;

namespace Api;

/// <summary>
/// Подписка мода на события мира (<see cref="IModContext.Events"/>). Подписываться можно только в <see cref="IMod.Load"/>.
/// </summary>
/// <remarks>
/// События сообщают об уже случившемся, отменить их нельзя. Обработчики вызываются в потоке сервера
/// после того, как действие закончено целиком (например, добыча уже в инвентаре), в порядке загрузки модов.
/// Исключение в обработчике пишется в лог с id мода и не мешает ни игре, ни другим обработчикам.
/// </remarks>
public interface IModEvents
{
    /// <summary>Игрок сломал блок (<see cref="IWorld.TryBreakBlock"/>).</summary>
    void OnBlockBroken(Action<BlockBroken> handler);

    /// <summary>Игрок поставил блок (<see cref="IWorld.TryPlaceBlock"/>).</summary>
    void OnBlockPlaced(Action<BlockPlaced> handler);

    /// <summary>Игрок вошёл в мир: позиция, режим и инвентарь уже восстановлены, клиент их получил.</summary>
    void OnPlayerJoined(Action<PlayerJoined> handler);

    /// <summary>Игрок сделал предмет по рецепту: ингредиенты уже потрачены, результат в инвентаре.</summary>
    void OnItemCrafted(Action<ItemCrafted> handler);
}

/// <param name="Block">Блок, который был на месте до ломания.</param>
public readonly record struct BlockBroken(IWorld World, IPlayer Player, BlockPos Position, BlockId Block);

/// <param name="Block">Поставленный блок.</param>
public readonly record struct BlockPlaced(IWorld World, IPlayer Player, BlockPos Position, BlockId Block);

public readonly record struct PlayerJoined(IWorld World, IPlayer Player);

/// <param name="Times">
/// Сколько раз рецепт выполнен этим действием (Shift+клик делает несколько за раз); получено
/// <c>Recipe.Result.Count × Times</c> штук.
/// </param>
public readonly record struct ItemCrafted(IWorld World, IPlayer Player, Recipe Recipe, int Times);
