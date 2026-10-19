using Shared.Lighting;

namespace Shared.Tests.Lighting;

public class DayCycleTests
{
    private const float Night = DayCycle.NightSkyBrightness;

    [Fact]
    public void Lengths_AreThirtyMinutesDay_TenMinutesNight()
    {
        Assert.Equal(30 * 60 * 20, DayCycle.DayTicks);
        Assert.Equal(10 * 60 * 20, DayCycle.NightTicks);
        Assert.Equal(48_000, DayCycle.LengthTicks);
        Assert.Equal(DayCycle.LengthTicks, DayCycle.HourTicks * 24);
    }

    [Fact]
    public void Brightness_NoonIsFull_MidnightIsNight()
    {
        Assert.Equal(1f, DayCycle.SkyBrightness(DayCycle.DayTicks / 2));
        Assert.Equal(Night, DayCycle.SkyBrightness(DayCycle.DayTicks + DayCycle.NightTicks / 2));
    }

    [Fact]
    public void Brightness_Transitions_AreOneMinuteAtDayEdges()
    {
        const int T = DayCycle.TransitionTicks;
        // Рассвет — первая минута дня: с ночной до полной.
        Assert.Equal(Night, DayCycle.SkyBrightness(0));
        Assert.InRange(DayCycle.SkyBrightness(T / 2), Night + 0.1f, 0.9f);
        Assert.Equal(1f, DayCycle.SkyBrightness(T));
        // Закат — последняя минута дня.
        Assert.Equal(1f, DayCycle.SkyBrightness(DayCycle.DayTicks - T));
        Assert.InRange(DayCycle.SkyBrightness(DayCycle.DayTicks - T / 2), Night + 0.1f, 0.9f);
        Assert.Equal(Night, DayCycle.SkyBrightness(DayCycle.DayTicks));
    }

    [Fact]
    public void Brightness_IsContinuous_ThroughWholeCycleAndItsSeam()
    {
        // Две сутки подряд по тику (и через стык суток): никаких скачков. Наибольший шаг гладкого перехода —
        // 1,5 × (1 − ночная) / длина перехода.
        var maxStep = 1.5 * (1 - Night) / DayCycle.TransitionTicks * 1.01;
        var previous = DayCycle.SkyBrightness(-1);
        for (var t = 0; t <= 2 * DayCycle.LengthTicks; t++)
        {
            var current = DayCycle.SkyBrightness(t);
            Assert.True(Math.Abs(current - previous) <= maxStep, $"Скачок яркости на тике {t}: {previous} → {current}");
            previous = current;
        }
    }

    [Fact]
    public void Sun_RisesEast_PeaksAtNoon_SetsWest_ThenGoesUnder()
    {
        Assert.Equal(0, DayCycle.SunAngle(0), 6);
        Assert.Equal(Math.PI / 2, DayCycle.SunAngle(DayCycle.DayTicks / 2), 6);
        Assert.Equal(Math.PI, DayCycle.SunAngle(DayCycle.DayTicks), 6);
        Assert.Equal(1.5 * Math.PI, DayCycle.SunAngle(DayCycle.DayTicks + DayCycle.NightTicks / 2), 6);
        // Стык суток: перед рассветом — почти 2π, то есть снова у восточного горизонта.
        Assert.Equal(2 * Math.PI, DayCycle.SunAngle(DayCycle.LengthTicks - 0.001), 4);
    }

    [Fact]
    public void TimeOfDay_WrapsEveryCycle()
    {
        Assert.Equal(5, DayCycle.TimeOfDay(5));
        Assert.Equal(5, DayCycle.TimeOfDay(3L * DayCycle.LengthTicks + 5));
        Assert.Equal(DayCycle.LengthTicks - 5, DayCycle.TimeOfDay(-5));
        Assert.True(DayCycle.IsDay(DayCycle.DayTicks - 1));
        Assert.False(DayCycle.IsDay(DayCycle.DayTicks));
    }

    [Fact]
    public void NewWorld_StartsInFullMorning()
    {
        Assert.True(DayCycle.IsDay(DayCycle.NewWorldTime));
        Assert.Equal(1f, DayCycle.SkyBrightness(DayCycle.NewWorldTime));
        Assert.True(DayCycle.SunAngle(DayCycle.NewWorldTime) < Math.PI / 4, "Утром солнце ещё низко на востоке");
    }

    [Theory]
    [InlineData(0, "день 0:00 из 30:00")]
    [InlineData(20 * 61, "день 1:01 из 30:00")]
    [InlineData(DayCycle.DayTicks + 20 * 300, "ночь 5:00 из 10:00")]
    public void Describe_ShowsPhaseAndMinutes(long time, string expected) => Assert.Equal(expected, DayCycle.Describe(time));
}
