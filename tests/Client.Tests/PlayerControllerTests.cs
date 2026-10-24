using Engine.Graphics;
using Shared;
using Shared.Blocks;
using Shared.Physics;
using Shared.Voxels;

namespace Client.Tests;

public class PlayerControllerTests
{
    [Fact]
    public void DeathFreezeKeepsCameraStillAcrossInterpolationCycles()
    {
        var chunks = new ChunkMap();
        chunks.Set(new Chunk(new ChunkPos(0,0,0)));
        var blocks = new BlockRegistry();
        blocks.Freeze();
        var controller = new PlayerController(new Camera(), chunks, blocks);
        controller.Teleport(new(5,10,5));
        controller.Tick(null!, controlsEnabled:false);
        Assert.True(controller.Position.Y < 10);
        controller.Freeze();
        var eye = controller.Position + new Vector3d(0,PlayerPhysics.EyeHeight,0);
        foreach(var alpha in new[]{0f,0.5f,1f,0f,0.5f})
        {
            controller.UpdateEye(alpha);
            Assert.Equal(eye, controller.EyePosition);
        }
    }
}
