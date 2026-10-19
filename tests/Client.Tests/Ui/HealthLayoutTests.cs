using System.Numerics;
using Client.Ui;

namespace Client.Tests.Ui;

/// <summary>Полоса здоровья и экран смерти: геометрия в целых пикселях, без наложений.</summary>
public class HealthLayoutTests
{
    private const int MaxHealth = 20;

    [Theory]
    [InlineData(1280, 720, 3)]
    [InlineData(1920, 1080, 4)]
    [InlineData(800, 600, 2)]
    [InlineData(321, 241, 1)]
    public void HealthBar_AboveHotbarLeftEdge_FourPixelsPerUnit(int width, int height, int scale)
    {
        var hotbar = HudLayout.Hotbar(width, height, scale);
        var bar = HudLayout.HealthBar(hotbar, MaxHealth, scale);

        Assert.Equal(hotbar.X, bar.X);
        Assert.Equal(hotbar.Y - HudLayout.HealthBarGap * scale, bar.Bottom); // зазор до хотбара
        Assert.Equal((MaxHealth * HudLayout.HealthUnit + 2) * scale, bar.Width);
        Assert.True(bar.Right <= hotbar.Right);

        // Полная заливка — ровно внутренность рамки; каждая единица — 4 пикселя интерфейса.
        var full = HudLayout.HealthFill(bar, MaxHealth, scale);
        Assert.Equal((bar.X + scale, bar.Y + scale, bar.Width - 2 * scale, bar.Height - 2 * scale), (full.X, full.Y, full.Width, full.Height));
        Assert.Equal(13 * HudLayout.HealthUnit * scale, HudLayout.HealthFill(bar, 13, scale).Width);
        Assert.Equal(0, HudLayout.HealthFill(bar, 0, scale).Width);
        Assert.Equal(0, HudLayout.HealthFill(bar, -3, scale).Width);
    }

    /// <summary>Название предмета в руке стоит выше полосы здоровья, а не на ней.</summary>
    [Theory]
    [InlineData(1, 7)]
    [InlineData(3, 21)]
    public void HeldName_IsAboveHealthBar(int scale, int ascent)
    {
        var hotbar = HudLayout.Hotbar(1280, 720, scale);
        var bar = HudLayout.HealthBar(hotbar, MaxHealth, scale);

        var nameTop = HudLayout.HeldNameBottom(hotbar, scale) - ascent;

        Assert.True(HudLayout.HeldNameBottom(hotbar, scale) < bar.Y);
        Assert.True(nameTop >= 0);
    }

    [Theory]
    [InlineData(1280, 720, 3)]
    [InlineData(800, 600, 2)]
    public void RespawnButton_CenteredBelowMiddle_AndClickable(int width, int height, int scale)
    {
        var button = HudLayout.RespawnButton(width, height, scale);

        Assert.Equal(width - button.Right, button.X); // по центру
        Assert.True(button.Y > height / 2);
        Assert.True(button.Contains(new Vector2(width / 2f, button.Y + 1)));
        Assert.False(button.Contains(new Vector2(width / 2f, height / 2f)));
    }

    [Fact]
    public void DeathCause_KnownAndUnknown()
    {
        Assert.Equal("Разбился, упав с высоты", DeathCauses.Text("base:fall"));
        Assert.Equal("Погиб", DeathCauses.Text("my_mod:laser"));
    }
}
