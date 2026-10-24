using Client.Rendering;

namespace Client.Tests.Rendering;

public class ItemEntityRendererTests
{
    [Theory]
    [InlineData(true, -Math.PI / 2)]
    [InlineData(true, Math.PI / 2)]
    [InlineData(false, -Math.PI / 2)]
    public void FloatingItemNeverIntersectsSupportingGround(bool billboard,double phase)
    {
        var halfHeight = billboard ? 0.2f : 0.125f;
        var bottom = ItemEntityRenderer.FloatingHeight(billboard,phase/2,0)-halfHeight;
        Assert.True(bottom > 0, $"Нижний край предмета должен оставаться над опорой, y={bottom}");
    }
}
