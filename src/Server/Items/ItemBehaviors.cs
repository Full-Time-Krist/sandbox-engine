using Shared.Items;

namespace Server.Items;

/// <summary>
/// Выбор поведения по предмету. Пока предметы — только блоки и пустая рука.
/// На этапе 4 здесь будет таблица «предмет → поведение», которую заполняют контент и моды.
/// </summary>
internal static class ItemBehaviors
{
    public static IItemBehavior For(ItemDefinition item) =>
        item.Block != null ? BlockItemBehavior.Instance : HandBehavior.Instance;
}
