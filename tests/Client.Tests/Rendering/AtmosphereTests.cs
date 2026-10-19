using System.Numerics;
using Client.Rendering;
using Shared.Lighting;

namespace Client.Tests.Rendering;

public class AtmosphereTests
{
    private const float ViewBlocks = 192;

    [Fact]
    public void Noon_IsDaySky_FullBrightness()
    {
        var noon = Atmosphere.At(DayCycle.DayTicks / 2, ViewBlocks);

        Assert.Equal(1f, noon.SkyBrightness);
        Assert.Equal(Atmosphere.DayColor, noon.SkyColor);
    }

    [Fact]
    public void Midnight_IsNightSky()
    {
        var midnight = Atmosphere.At(DayCycle.DayTicks + DayCycle.NightTicks / 2, ViewBlocks);

        Assert.Equal(DayCycle.NightSkyBrightness, midnight.SkyBrightness);
        Assert.Equal(Atmosphere.NightColor, midnight.SkyColor);
    }

    [Theory]
    [InlineData(DayCycle.TransitionTicks / 2)]                    // рассвет
    [InlineData(DayCycle.DayTicks - DayCycle.TransitionTicks / 2)] // закат
    public void Transitions_AreReddish(int time)
    {
        var color = Atmosphere.At(time, ViewBlocks).SkyColor;

        Assert.True(color.X > color.Z, $"На рассвете и закате красного больше синего: {color}");
        Assert.True(color.X > Atmosphere.DayColor.X, $"Краснее дневного неба: {color}");
    }

    [Fact]
    public void SkyColor_IsContinuous_ThroughCycle()
    {
        var previous = Atmosphere.At(-1, ViewBlocks).SkyColor;
        for (var t = 0; t <= DayCycle.LengthTicks; t++)
        {
            var current = Atmosphere.At(t, ViewBlocks).SkyColor;
            Assert.True(Vector3.Distance(previous, current) < 0.01f, $"Скачок цвета неба на тике {t}: {previous} → {current}");
            previous = current;
        }
    }

    [Fact]
    public void Fog_CoversEverythingAtViewDistance()
    {
        var atmosphere = Atmosphere.At(0, ViewBlocks);

        Assert.Equal(ViewBlocks, atmosphere.FogEnd);
        Assert.Equal(ViewBlocks * Atmosphere.FogStartShare, atmosphere.FogStart);
    }

    [Fact]
    public void Sun_RisesInEast_OverheadAtNoon_SetsInWest_MoonOpposite()
    {
        var morning = SkyRenderer.SunDirection(DayCycle.TransitionTicks);
        var noon = SkyRenderer.SunDirection(DayCycle.DayTicks / 2);
        var evening = SkyRenderer.SunDirection(DayCycle.DayTicks - DayCycle.TransitionTicks);
        var midnight = SkyRenderer.SunDirection(DayCycle.DayTicks + DayCycle.NightTicks / 2);

        Assert.True(morning.X > 0.9f && morning.Y > 0, $"Утром солнце низко на востоке (+X): {morning}");
        Assert.True(noon.Y > 0.999f, $"В полдень — в зените: {noon}");
        Assert.True(evening.X < -0.9f && evening.Y > 0, $"Вечером — низко на западе: {evening}");
        Assert.True(midnight.Y < -0.999f, "В полночь солнце под землёй");
        Assert.True(SkyRenderer.AboveHorizon(-midnight, 4), "…а луна напротив него — в зените");
        Assert.False(SkyRenderer.AboveHorizon(midnight, 6));
        Assert.All([morning, noon, evening, midnight], d => Assert.Equal(1f, d.Length(), 4));
    }
}
