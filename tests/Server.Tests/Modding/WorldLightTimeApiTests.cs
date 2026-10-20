using Api;
using Shared;
using Shared.Content;
using Shared.Lighting;
using Shared.Voxels;

namespace Server.Tests.Modding;

/// <summary>API модов для света и времени (шаг 8.7): <see cref="IWorld.GetLight"/>, <see cref="IWorld.Time"/>.</summary>
public sealed class WorldLightTimeApiTests : IDisposable
{
    private readonly GameServer _server = TestServers.Create();
    private readonly TestServers.TestClient _client;

    public WorldLightTimeApiTests() => _client = TestServers.JoinAndLoad(_server);

    public void Dispose() => _server.Dispose();

    /// <summary>Свет — тот же, что считает сервер: над землёй небо 15; у факела — его 15 и 14 рядом; в незагруженном — 0.</summary>
    [Fact]
    public void GetLight_IsServerLight()
    {
        var world = _server.ModWorld;
        var surface = TestServers.TopBlock(_server.World, 0, 0);
        var torch = GameContent.Load().Blocks.Get("base:torch").Id;
        Assert.True(world.TryPlaceBlock(surface + new BlockPos(0, 1, 0), torch, _server.ModPlayer(_client.PlayerId)));

        Assert.Equal(new LightLevel(15, 15), world.GetLight(surface + new BlockPos(0, 1, 0)));
        Assert.Equal(new LightLevel(15, 14), world.GetLight(surface + new BlockPos(0, 2, 0)));
        Assert.Equal(new LightLevel(0, 0), world.GetLight(new BlockPos(1_000_000, 0, 0)));
    }

    /// <summary>Время — часы мира сервера: тот же тик, утро нового мира — день, через 30 минут — ночь, тёмное небо.</summary>
    [Fact]
    public void Time_IsWorldClock()
    {
        var time = _server.ModWorld.Time;

        Assert.Equal(_server.WorldTime, time.Ticks);
        Assert.True(time.IsDay);
        Assert.Equal(1f, time.SkyBrightness);
        Assert.Equal(DayCycle.Describe(time.Ticks), time.ToString());

        var night = new WorldTime(DayCycle.DayTicks + DayCycle.TransitionTicks);
        Assert.False(night.IsDay);
        Assert.Equal(DayCycle.NightSkyBrightness, night.SkyBrightness);
        Assert.Equal(0.75 + 1.0 / 40, night.DayFraction, 1e-9);
        // Насколько светло: ночью небо 15 даёт 4, а факел — свои 15.
        Assert.Equal(4f, new LightLevel(15, 0).Effective(night), 4);
        Assert.Equal(15f, new LightLevel(15, 15).Effective(night));
    }
}
