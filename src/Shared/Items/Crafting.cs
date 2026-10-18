using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Items;

/// <summary>
/// Правила ремесла, общие для сервера (он решает и делает) и клиента (он показывает, какие рецепты доступны).
/// </summary>
public static class Crafting
{
    /// <summary>
    /// Как далеко может стоять станок: от глаза игрока до ближайшей точки блока, в блоках. Столько же, сколько досягаемость
    /// блока (<see cref="GameRules.BlockReach"/>): поставленным станком можно сразу пользоваться. С 4 блоками (шаг 6.4)
    /// при прохождении верстак, поставленный в конец туннеля, оказывался «не рядом».
    /// </summary>
    public const float StationReach = GameRules.BlockReach;

    /// <summary>
    /// Сколько раз подряд хватит ингредиентов из слотов инвентаря (курсор не считается). Место под результат
    /// не проверяется: его проверяет сам обмен (<see cref="Inventory.TryExchange"/>).
    /// </summary>
    public static int MaxCrafts(Inventory inventory, Recipe recipe)
    {
        var times = int.MaxValue;
        foreach (var ingredient in recipe.Ingredients)
            times = Math.Min(times, inventory.Count(ingredient.Item) / ingredient.Count);
        return times;
    }

    /// <summary>
    /// Стоит ли рядом с глазом блок <paramref name="station"/>: ближайшая точка его куба не дальше
    /// <see cref="StationReach"/>. Незагруженный мир станков не содержит.
    /// </summary>
    public static bool IsStationNear(ChunkMap chunks, Vector3d eye, BlockId station)
    {
        var reach = (int)Math.Ceiling(StationReach);
        var center = BlockPos.Containing(eye);
        for (var x = center.X - reach; x <= center.X + reach; x++)
        for (var y = center.Y - reach; y <= center.Y + reach; y++)
        for (var z = center.Z - reach; z <= center.Z + reach; z++)
        {
            if (chunks.GetBlock(new BlockPos(x, y, z)) != station)
                continue;
            // Ближайшая точка куба [x, x + 1] — глаз, прижатый к нему по каждой оси.
            var dx = eye.X - Math.Clamp(eye.X, x, x + 1);
            var dy = eye.Y - Math.Clamp(eye.Y, y, y + 1);
            var dz = eye.Z - Math.Clamp(eye.Z, z, z + 1);
            if (dx * dx + dy * dy + dz * dz <= StationReach * StationReach)
                return true;
        }
        return false;
    }
}
