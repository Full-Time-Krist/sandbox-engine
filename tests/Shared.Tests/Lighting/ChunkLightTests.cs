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

    [Fact]
    public void CopyFrom_SameEverywhere_StaysUniform()
    {
        var light = new ChunkLight();
        light.SetSky(7, 3);
        var values = new byte[ChunkMath.Volume];
        Array.Fill(values, ChunkLight.Pack(15, 2));

        light.CopyFrom(values);

        Assert.True(light.IsUniform);
        Assert.Equal(ChunkLight.Pack(15, 2), light.UniformValue);
    }

    [Fact]
    public void CopyFrom_Different_KeepsEveryValue()
    {
        var light = new ChunkLight();
        var values = new byte[ChunkMath.Volume];
        for (var i = 0; i < values.Length; i++)
            values[i] = (byte)(i % 251);

        light.CopyFrom(values);

        Assert.False(light.IsUniform);
        var copy = new byte[ChunkMath.Volume];
        light.CopyTo(copy);
        Assert.Equal(values, copy);
    }
}
