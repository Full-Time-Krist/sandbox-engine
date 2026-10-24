using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Physics;

/// <summary>Позиция нижней грани, скорость и опора предмета на земле.</summary>
public readonly record struct ItemBodyState(Vector3d Position, Vector3 Velocity = default, bool OnGround = false);

public static class ItemPhysics
{
    public const double Size = 0.25;
    public static Aabb BodyAt(Vector3d position) => new(
        position + new Vector3d(-Size / 2, 0, -Size / 2),
        position + new Vector3d(Size / 2, Size, Size / 2));

    public static ItemBodyState Step(ItemBodyState state, ChunkMap chunks, BlockRegistry blocks)
    {
        const float dt = (float)GameTime.TickDuration;
        var velocity = state.Velocity;
        velocity.Y = MathF.Max(velocity.Y - PlayerPhysics.Gravity * dt, -PlayerPhysics.TerminalVelocity);
        var friction = state.OnGround ? 0.65f : 0.99f;
        velocity.X *= friction;
        velocity.Z *= friction;
        var wanted = new Vector3d(velocity.X * GameTime.TickDuration, velocity.Y * GameTime.TickDuration, velocity.Z * GameTime.TickDuration);
        if (VoxelPhysics.Sweep(BodyAt(state.Position), wanted, chunks, blocks) is not { } moved)
            return state with { Velocity = Vector3.Zero };
        if (moved.X != wanted.X) velocity.X = 0;
        if (moved.Y != wanted.Y) velocity.Y = 0;
        if (moved.Z != wanted.Z) velocity.Z = 0;
        return new(state.Position + moved, velocity, moved.Y != wanted.Y && wanted.Y < 0);
    }
}
