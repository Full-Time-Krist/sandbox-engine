using Client.Ui;
using Engine.Ui;
using Shared.Items;

namespace Client.Tests.Ui;

public class HudLayoutTests
{
    [Theory]
    [InlineData(1280, 720, 3)]
    [InlineData(1920, 1080, 4)]
    [InlineData(800, 600, 2)]
    [InlineData(2560, 1440, 6)]
    [InlineData(320, 240, 1)]
    [InlineData(100, 50, 1)]    // окно меньше минимума: масштаб всё равно 1, а не 0
    [InlineData(1920, 500, 2)]  // узкое по высоте окно ограничивает масштаб
    public void Scale_IsLargestWholeThatFits(int width, int height, int expected)
    {
        Assert.Equal(expected, UiScale.For(width, height));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1281, 721)]
    [InlineData(800, 600)]
    [InlineData(1920, 1080)]
    public void Hotbar_IsCenteredAtBottom(int width, int height)
    {
        var scale = UiScale.For(width, height);

        var bar = HudLayout.Hotbar(width, height, scale);

        Assert.Equal(HudLayout.HotbarWidth * scale, bar.Width);
        Assert.InRange(width - bar.Right - bar.X, 0, 1); // поровну слева и справа (до пикселя при нечётной разнице)
        Assert.Equal(height - HudLayout.HotbarMargin * scale, bar.Bottom);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Slots_AreContiguous_InsideHotbar_IconInsideSlot(int scale)
    {
        var bar = HudLayout.Hotbar(1280, 720, scale);

        for (var i = 0; i < Inventory.HotbarSize; i++)
        {
            var slot = HudLayout.Slot(bar, i, scale);
            Assert.Equal(bar.X + scale + i * HudLayout.SlotSize * scale, slot.X);
            Assert.True(slot.X >= bar.X && slot.Right <= bar.Right && slot.Y >= bar.Y && slot.Bottom <= bar.Bottom);

            var (x, y) = HudLayout.Icon(slot, scale);
            Assert.True(x > slot.X && x + ItemIcons.Size * scale < slot.Right);
            Assert.True(y > slot.Y && y + ItemIcons.Size * scale < slot.Bottom);

            // Рамка выбора охватывает ячейку и не заходит на иконку (её толщина — 2 пикселя интерфейса).
            var selection = HudLayout.Selection(slot, scale);
            Assert.True(selection.X < slot.X && selection.Right > slot.Right);
            Assert.True(selection.X + 2 * scale <= x);
        }
    }

    [Theory]
    [InlineData(1280, 720, 3)]
    [InlineData(1281, 721, 3)]
    [InlineData(800, 600, 2)]
    public void Crosshair_IsCenteredCross_WithoutOverlaps(int width, int height, int scale)
    {
        var bars = HudLayout.Crosshair(width, height, scale);

        // Каждый пиксель креста покрыт ровно одной чертой: двойная инверсия вернула бы цвет фона.
        var covered = new Dictionary<(int, int), int>();
        foreach (var bar in bars)
        for (var x = bar.X; x < bar.Right; x++)
        for (var y = bar.Y; y < bar.Bottom; y++)
            covered[(x, y)] = covered.GetValueOrDefault((x, y)) + 1;
        Assert.All(covered.Values, count => Assert.Equal(1, count));

        var length = HudLayout.CrosshairLength * scale;
        Assert.Equal(2 * length * scale - scale * scale, covered.Count); // две черты длиной 9 и толщиной 1, общий центр
        var minX = bars.Min(b => b.X);
        var maxX = bars.Max(b => b.Right);
        var minY = bars.Min(b => b.Y);
        var maxY = bars.Max(b => b.Bottom);
        Assert.Equal(length, maxX - minX);
        Assert.Equal(length, maxY - minY);
        Assert.InRange((minX + maxX) / 2.0 - width / 2.0, -scale, scale);
        Assert.InRange((minY + maxY) / 2.0 - height / 2.0, -scale, scale);
    }
}
