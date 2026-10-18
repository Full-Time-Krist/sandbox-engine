using Shared.Blocks;
using Shared.Items;

namespace Client.Ui;

/// <summary>Рецепт в списке на экране инвентаря: можно ли сделать его сейчас.</summary>
/// <param name="Times">Сколько раз хватит ингредиентов (без учёта станка).</param>
/// <param name="StationNear">Станок рядом или не нужен.</param>
internal readonly record struct RecipeEntry(Recipe Recipe, int Times, bool StationNear)
{
    public bool Available => Times > 0 && StationNear;
}

/// <summary>
/// Список рецептов для экрана инвентаря: доступные сейчас — сверху, остальные — ниже; внутри групп порядок реестра
/// (по именам). Клиент считает доступность по своей копии инвентаря и мира только для показа: решает сервер.
/// </summary>
internal static class RecipeList
{
    /// <param name="isStationNear">Стоит ли блок рядом с игроком. Вызывается не больше раза на каждый вид станка.</param>
    public static List<RecipeEntry> Build(RecipeRegistry recipes, Inventory inventory, Func<BlockId, bool> isStationNear)
    {
        // Поиск станка просматривает 9³ блоков вокруг глаза; станков всего несколько видов, а рецептов у каждого много.
        var stations = new Dictionary<BlockId, bool>();
        var entries = new List<RecipeEntry>(recipes.All.Length);
        foreach (var recipe in recipes.All)
        {
            var near = true;
            if (recipe.Station is { } station && !stations.TryGetValue(station, out near))
                stations[station] = near = isStationNear(station);
            entries.Add(new RecipeEntry(recipe, Crafting.MaxCrafts(inventory, recipe), near));
        }

        // Сортировка устойчивая: внутри «доступных» и «недоступных» остаётся порядок реестра.
        return [.. entries.OrderBy(entry => entry.Available ? 0 : 1)];
    }
}
