using Shared.Items;

namespace Client.Ui;

/// <summary>
/// Названия предметов для игрока — поле <c>displayName</c> в JSON предмета (ванильные и модов одинаково).
/// Переводы появятся позже; пока одна строка на предмет.
/// </summary>
internal sealed class ItemNames(IReadOnlyDictionary<string, string> displayNames)
{
    /// <summary>Название для игрока; у предмета без названия — его постоянное имя (<c>mod:item</c>), чтобы пропуск был виден.</summary>
    public string For(ItemDefinition item) => displayNames.GetValueOrDefault(item.Name) ?? item.Name;
}
