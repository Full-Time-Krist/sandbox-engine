using Shared.Lighting;
using Shared.Voxels;

namespace Shared.Tests.Lighting;

public class ChunkLightTests
{
    [Fact]
    public void Channels_AreIndependent()
    {
        var light = new ChunkLight();
        light.SetSky(5, 15);
        light.SetBlock(5, 7);
        light.SetSky(5, 3);

        Assert.Equal(3, light.GetSky(5));
        Assert.Equal(7, light.GetBlock(5));
        Assert.Equal(ChunkLight.Pack(3, 7), light.Get(5));
        Assert.Equal(0, light.Get(6));
    }

    [Fact]
    public void StaysUniform_UntilValueDiffers()
    {
        var light = new ChunkLight();
        light.Fill(ChunkLight.Pack(15, 0));
        light.SetSky(100, 15); // то же значение — массив не нужен
        Assert.True(light.IsUniform);
        Assert.Equal(15, light.GetSky(ChunkMath.Volume - 1));

        light.SetSky(100, 14);
        Assert.False(light.IsUniform);
        Assert.Equal(14, light.GetSky(100));
        Assert.Equal(15, light.GetSky(101)); // остальные сохранили общее значение

        var copy = new byte[ChunkMath.Volume];
        light.CopyTo(copy);
        Assert.Equal(ChunkLight.Pack(14, 0), copy[100]);
        Assert.Equal(ChunkLight.Pack(15, 0), copy[0]);

        light.Fill(0);
        Assert.True(light.IsUniform);
        light.CopyTo(copy);
        Assert.All(copy, b => Assert.Equal(0, b));
    }
}
