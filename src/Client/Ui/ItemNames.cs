using Shared.Items;

namespace Client.Ui;

/// <summary>
/// Названия предметов для игрока. Временно в коде, как внешний вид блоков (<see cref="Rendering.BlockVisuals"/>);
/// на этапе 4 они переедут в файлы контента вместе с переводами.
/// </summary>
internal static class ItemNames
{
    public static readonly IReadOnlyDictionary<string, string> Base = new Dictionary<string, string>
    {
        ["base:stone"] = "Камень",
        ["base:dirt"] = "Земля",
        ["base:grass"] = "Дёрн",
        ["base:sand"] = "Песок",
        ["base:log"] = "Бревно",
        ["base:planks"] = "Доски",
    };

    /// <summary>Название для игрока; у предмета без названия — его постоянное имя (<c>mod:item</c>), чтобы пропуск был виден.</summary>
    public static string For(ItemDefinition item) => Base.GetValueOrDefault(item.Name) ?? item.Name;
}
