using Shared.Blocks;

namespace Server.Items;

/// <summary>
/// Выбор поведения по предмету. Пока все предметы — блоки и ведут себя одинаково.
/// На этапе 4 здесь будет таблица «предмет → поведение», которую заполняют контент и моды.
/// </summary>
internal static class ItemBehaviors
{
    public static IItemBehavior For(BlockId item) => BlockItemBehavior.Instance;
}
