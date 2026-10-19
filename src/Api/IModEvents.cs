using Shared.Blocks;
using Shared.Items;
using Shared.Quests;
using Shared.Voxels;

namespace Api;

/// <summary>
/// Подписка мода на события мира (<see cref="IModContext.Events"/>). Подписываться можно только в <see cref="IMod.Load"/>.
/// </summary>
/// <remarks>
/// События сообщают об уже случившемся, отменить их нельзя. Обработчики вызываются в потоке сервера
/// после того, как действие закончено целиком (например, блок уже сломан и добыча выпала), в порядке загрузки модов.
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

    /// <summary>Игрок выполнил задание (только в выживании): клиенту уже ушло сообщение о нём.</summary>
    void OnQuestCompleted(Action<QuestCompleted> handler);

    /// <summary>Игрок подобрал предмет с земли: подобранное уже в инвентаре (остаток, если не влез, лежит дальше).</summary>
    void OnItemPickedUp(Action<ItemPickedUp> handler);

    /// <summary>Игрок получил урон (только в выживании): здоровье уже уменьшено, клиенту ушло новое.</summary>
    void OnPlayerDamaged(Action<PlayerDamaged> handler);

    /// <summary>Игрок погиб: его вещи уже выпали на месте смерти, клиенту ушёл экран смерти.</summary>
    void OnPlayerDied(Action<PlayerDied> handler);
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

public readonly record struct QuestCompleted(IWorld World, IPlayer Player, Quest Quest);

/// <param name="Stack">Сколько подобрано этим касанием.</param>
public readonly record struct ItemPickedUp(IWorld World, IPlayer Player, ItemStack Stack);

/// <param name="Amount">Сколько единиц здоровья отнято.</param>
/// <param name="Cause">Причина — имя вида <c>base:fall</c>.</param>
public readonly record struct PlayerDamaged(IWorld World, IPlayer Player, int Amount, string Cause);

/// <param name="Cause">Причина последнего урона — имя вида <c>base:fall</c>.</param>
public readonly record struct PlayerDied(IWorld World, IPlayer Player, string Cause);
