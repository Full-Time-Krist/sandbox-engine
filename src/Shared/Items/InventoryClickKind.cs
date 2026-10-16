namespace Shared.Items;

/// <summary>Какой клик игрок сделал по слоту на экране инвентаря (<see cref="Inventory.Click"/>).</summary>
public enum InventoryClickKind
{
    /// <summary>ЛКМ: взять стопку, положить, доложить в стопку того же предмета или поменять местами с курсором.</summary>
    Primary,

    /// <summary>ПКМ: взять половину стопки или положить одну штуку с курсора.</summary>
    Secondary,

    /// <summary>Shift+ЛКМ: перенести стопку из хотбара в основную часть или обратно, минуя курсор.</summary>
    QuickMove,
}
