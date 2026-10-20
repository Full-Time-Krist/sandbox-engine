using System.Numerics;
using Shared;
using Shared.Blocks;
using Shared.Entities;
using Shared.Mobs;
using Shared.Physics;
using Shared.Voxels;

namespace Server.Entities;

/// <summary>Моб: какого он вида и куда сейчас хочет идти (это задаёт поведение; шаг 9.3).</summary>
internal struct Mob
{
    public MobId Kind;

    /// <summary>Желаемое направление ходьбы по горизонтали (X — мировой X, Y — мировой Z), длина до 1; ноль — стоит.</summary>
    public Vector2 Move;
}

/// <summary>Состояние ходьбы моба между тиками — то же, что у игрока (<see cref="PlayerState"/> без позиции и полёта).</summary>
internal struct Walk
{
    public Vector3 Velocity;
    public bool OnGround;
}

/// <summary>Куда смотрит сущность: поворот тела и головы относительно мира, радианы — как у камеры (0 — на −Z, π/2 — на +X).</summary>
internal struct Facing
{
    public float Yaw;
    public float HeadYaw;
}

/// <summary>Создание мобов и их ходьба (шаг 9.1). Поведение — шаг 9.3, бой — 9.4, появление и сохранение — 9.5.</summary>
internal static class MobEntities
{
    /// <summary>Моб как ходок: короб и скорость его вида; бега у мобов пока нет.</summary>
    public static Walker WalkerOf(MobDefinition definition) =>
        new(definition.Width, definition.Height, definition.Speed, definition.Speed);

    /// <summary>Моб вида <paramref name="definition"/> ногами в <paramref name="feet"/>, повёрнутый на <paramref name="yaw"/>.</summary>
    public static EntityId Spawn(EntityWorld entities, MobDefinition definition, Vector3d feet, float yaw)
    {
        var entity = entities.Create();
        entities.Set(entity, new Position { Value = feet });
        entities.Set(entity, new Mob { Kind = definition.Id });
        entities.Set(entity, new Walk());
        entities.Set(entity, new Facing { Yaw = yaw, HeadYaw = yaw });
        entities.Set(entity, new Health { Current = definition.Health, Max = definition.Health });
        entities.Set(entity, new Synced { SentPosition = feet, SentYaw = yaw });
        return entity;
    }

    /// <summary>
    /// Можно ли поставить моба вида <paramref name="definition"/> ногами в <paramref name="feet"/>: чанки под телом загружены
    /// и тело ни во что не упирается.
    /// </summary>
    public static bool Fits(MobDefinition definition, Vector3d feet, ChunkMap chunks, BlockRegistry blocks)
    {
        var body = WalkerOf(definition).BodyAt(feet);
        var (min, max) = (BlockPos.Containing(body.Min), BlockPos.Containing(body.Max));
        for (var cy = min.Y >> ChunkMath.Bits; cy <= max.Y >> ChunkMath.Bits; cy++)
        for (var cx = min.X >> ChunkMath.Bits; cx <= max.X >> ChunkMath.Bits; cx++)
        for (var cz = min.Z >> ChunkMath.Bits; cz <= max.Z >> ChunkMath.Bits; cz++)
        {
            if (!chunks.Contains(new ChunkPos(cx, cy, cz)))
                return false;
        }
        return !PlayerPhysics.Collides(WalkerOf(definition), feet, chunks, blocks);
    }

    /// <summary>
    /// Тик ходьбы всех мобов: та же физика, что у игрока (<see cref="PlayerPhysics.Step(PlayerState, PlayerInput, Walker, ChunkMap, BlockRegistry)"/>),
    /// с подъёмом на уступ по направлению движения. Идущий моб поворачивается туда, куда идёт. В незагруженном чанке —
    /// стоит, как игрок.
    /// </summary>
    public static void Move(EntityWorld entities, MobRegistry mobs, ChunkMap chunks, BlockRegistry blocks)
    {
        foreach (var entity in entities.Query<Mob, Walk>())
        {
            ref var position = ref entities.Get<Position>(entity);
            ref var walk = ref entities.Get<Walk>(entity);
            ref var facing = ref entities.Get<Facing>(entity);
            var mob = entities.Get<Mob>(entity);
            var move = mob.Move;
            var next = PlayerPhysics.Step(new PlayerState(position.Value, walk.Velocity, walk.OnGround),
                new PlayerInput(move, Facing: move), WalkerOf(mobs.Get(mob.Kind)), chunks, blocks);
            position.Value = next.Position;
            walk.Velocity = next.Velocity;
            walk.OnGround = next.OnGround;
            if (move.LengthSquared() > 1e-6f)
            {
                // Как у камеры: направление (sin yaw, −cos yaw) по X и Z.
                facing.Yaw = MathF.Atan2(move.X, -move.Y);
                facing.HeadYaw = facing.Yaw;
            }
        }
    }
}
