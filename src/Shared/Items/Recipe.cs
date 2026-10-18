using System.Collections.Immutable;
using Shared.Blocks;

namespace Shared.Items;

/// <summary>Рецепт: из каких предметов что получается и у какого станка.</summary>
/// <param name="Name">Постоянное имя вида <c>namespace:name</c>: по нему клиент просит сделать предмет, моды заменяют рецепт.</param>
/// <param name="Result">Что получается за один раз. Не больше предела стопки предмета.</param>
/// <param name="Ingredients">Что тратится за один раз: разные предметы, у каждого не меньше одной штуки.</param>
/// <param name="Station">Блок, который должен стоять рядом (<see cref="Crafting.StationReach"/>), или null — делается в руках.</param>
public sealed record Recipe(string Name, ItemStack Result, ImmutableArray<ItemStack> Ingredients, BlockId? Station);

/// <summary>
/// Все рецепты, по порядку имён (ordinal). Порядок не зависит от файлов, поэтому у клиента и сервера он одинаковый.
/// </summary>
public sealed class RecipeRegistry
{
    private readonly Dictionary<string, Recipe> _byName;

    /// <param name="recipes">Рецепты с разными именами, в любом порядке.</param>
    public RecipeRegistry(IEnumerable<Recipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        All = [.. recipes.OrderBy(recipe => recipe.Name, StringComparer.Ordinal)];
        _byName = All.ToDictionary(recipe => recipe.Name, StringComparer.Ordinal);
    }

    public ImmutableArray<Recipe> All { get; }

    public bool TryGet(string name, out Recipe recipe) => _byName.TryGetValue(name, out recipe!);

    public Recipe Get(string name) =>
        _byName.TryGetValue(name, out var recipe) ? recipe : throw new KeyNotFoundException($"Рецепт '{name}' не найден.");
}
