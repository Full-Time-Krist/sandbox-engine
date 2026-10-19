using Shared.Blocks;
using Shared.Entities;
using Shared.Physics;
using Shared.Voxels;

namespace Server.Entities;

/// <summary>Двигает падающие тела (<see cref="Position"/> + <see cref="Body"/>) на один тик: гравитация, столкновения, трение.</summary>
internal static class MovementSystem
{
    public static void Run(EntityWorld entities, ChunkMap chunks, BlockRegistry blocks)
    {
        foreach (var entity in entities.Query<Position, Body>())
        {
            ref var position = ref entities.Get<Position>(entity);
            ref var body = ref entities.Get<Body>(entity);
            var next = BoxPhysics.Fall(new BodyState(position.Value, body.Velocity, body.OnGround), body.Shape, chunks, blocks);
            position.Value = next.Position;
            body.Velocity = next.Velocity;
            body.OnGround = next.OnGround;
        }
    }
}
