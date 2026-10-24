using System.Numerics;
using Engine.Ui;
using Shared.Protocol;
using Silk.NET.Maths;

namespace Client.Tests.Ui;

public class HealthViewTests
{
    [Fact]
    public void DeathBlocksControlsAndRespawnOnlySendsOnceUntilServerRevives()
    {
        var view = new Client.Ui.HealthView();
        view.Apply(new HealthChanged(0, 20, true, "падение"));
        Assert.False(view.CanControl);
        var button = Client.Ui.HealthView.RespawnButton(new(1280,720), 3);
        Assert.False(view.TryRespawn(new(-1,-1), true, new(1280,720),3));
        Assert.True(view.TryRespawn(new(button.X+1,button.Y+1), true, new(1280,720),3));
        Assert.False(view.TryRespawn(new(button.X+1,button.Y+1), true, new(1280,720),3));
        Assert.False(view.CanControl);
        view.Apply(new HealthChanged(20));
        Assert.True(view.CanControl);
    }

    [Theory]
    [InlineData(640,480)]
    [InlineData(1280,720)]
    [InlineData(1920,1080)]
    public void DeathButtonFitsScreenAndContainsCenter(int width,int height)
    {
        var scale = UiScale.For(width,height);
        var button = Client.Ui.HealthView.RespawnButton(new(width,height),scale);
        Assert.True(button.X >= 0 && button.Y >=0 && button.Right <= width && button.Bottom <=height);
        Assert.True(button.Contains(new Vector2(width/2, button.Y+button.Height/2)));
    }

    [Fact]
    public void OddHealthDrawsOneHalfHeartAndLeavesRemainingHeartsEmpty()
    {
        using var font = TrueTypeFont.Load(UiAtlasTests.FontPath);
        var canvas = new UiCanvas(UiAtlas.Build(font,7,UiAtlas.DefaultCharacters));
        Client.Ui.HealthView.DrawHearts(canvas,new(640,480),1,3,20);
        var red = new UiColor(235,55,65).Packed;
        // Один полный силуэт (27 пикселей) и левая половина следующего (11).
        Assert.Equal(38,canvas.Vertices.ToArray().Count(v => v.Color == red)/4);
    }
}
