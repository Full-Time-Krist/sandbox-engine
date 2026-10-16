using Shared.Items;

namespace Client.Ui;

/// <summary>Прямоугольник в пикселях экрана.</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Где на экране лежат части интерфейса. Размеры заданы в пикселях интерфейса и умножаются на целый масштаб
/// (<see cref="Engine.Ui.UiScale"/>), результат — в пикселях экрана, всегда целых.
/// </summary>
internal static class HudLayout
{
    /// <summary>Шаг ячеек хотбара: иконка 16 и по 2 пикселя вокруг.</summary>
    public const int SlotSize = 20;

    /// <summary>Хотбар: 9 ячеек и рамка в пиксель вокруг — 182 × 22, как в Minecraft.</summary>
    public const int HotbarWidth = Inventory.HotbarSize * SlotSize + 2;
    public const int HotbarHeight = SlotSize + 2;

    /// <summary>Отступ хотбара от нижнего края экрана.</summary>
    public const int HotbarMargin = 2;

    /// <summary>Перекрестие: две черты длиной 9 и толщиной 1.</summary>
    public const int CrosshairLength = 9;

    /// <summary>Хотбар по центру внизу экрана.</summary>
    public static PixelRect Hotbar(int screenWidth, int screenHeight, int scale)
    {
        var width = HotbarWidth * scale;
        var height = HotbarHeight * scale;
        return new PixelRect((screenWidth - width) / 2, screenHeight - height - HotbarMargin * scale, width, height);
    }

    /// <summary>Ячейка хотбара номер <paramref name="index"/> (0–8).</summary>
    public static PixelRect Slot(PixelRect hotbar, int index, int scale) =>
        new(hotbar.X + (1 + index * SlotSize) * scale, hotbar.Y + scale, SlotSize * scale, SlotSize * scale);

    /// <summary>Левый верхний угол иконки в ячейке: по 2 пикселя от краёв.</summary>
    public static (int X, int Y) Icon(PixelRect slot, int scale) => (slot.X + 2 * scale, slot.Y + 2 * scale);

    /// <summary>Рамка выбранной ячейки: на пиксель шире ячейки с каждой стороны, чтобы не закрывать иконку.</summary>
    public static PixelRect Selection(PixelRect slot, int scale) =>
        new(slot.X - scale, slot.Y - scale, slot.Width + 2 * scale, slot.Height + 2 * scale);

    /// <summary>
    /// Перекрестие в центре экрана: горизонтальная черта целиком, вертикальная — двумя частями над ней и под ней.
    /// Черты не перекрываются: при инвертирующем смешивании двойная инверсия вернула бы в центре цвет фона.
    /// </summary>
    public static PixelRect[] Crosshair(int screenWidth, int screenHeight, int scale)
    {
        var length = CrosshairLength * scale;
        var x = screenWidth / 2 - length / 2;
        var y = screenHeight / 2 - length / 2;
        var middle = (CrosshairLength / 2) * scale; // смещение средней строки и столбца от начала черт
        return
        [
            new PixelRect(x, y + middle, length, scale),
            new PixelRect(x + middle, y, scale, middle),
            new PixelRect(x + middle, y + middle + scale, scale, length - middle - scale),
        ];
    }
}
