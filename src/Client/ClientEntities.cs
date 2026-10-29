using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Mobs;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Client;

/// <summary>Сущность глазами клиента: что рисовать и где она была (и куда смотрела) в двух последних тиках.</summary>
internal sealed class ClientEntity(EntityKind kind, ItemStack item, Vector3d position, MobId mob = default, float yaw = 0, float headTurn = 0)
{
    /// <summary>Скорость по горизонтали, с которой лапы качаются во весь размах, блоков в секунду (зомби — 2,3, овца — 1,8).</summary>
    public const float FullSwingSpeed = 2f;

    /// <summary>
    /// Какую долю расстояния до нового размаха проходит размах за тик: моб, который пошёл или остановился, разгоняет
    /// и гасит шаг за несколько тиков, а не дёргается.
    /// </summary>
    public const float SwingSmoothing = 0.4f;

    public EntityKind Kind { get; } = kind;
    public ItemStack Item { get; } = item;

    /// <summary>Вид моба (<see cref="EntityKind.Mob"/>).</summary>
    public MobId Mob { get; } = mob;

    public Vector3d Previous { get; set; } = position;
    public Vector3d Current { get; set; } = position;
    public float PreviousYaw { get; set; } = yaw;
    public float CurrentYaw { get; set; } = yaw;
    public float PreviousHeadTurn { get; set; } = headTurn;
    public float CurrentHeadTurn { get; set; } = headTurn;

    /// <summary>Путь по горизонтали, пройденный с появления у клиента, блоков: по нему качаются лапы (фаза шага).</summary>
    public float PreviousWalk { get; set; }
    public float CurrentWalk { get; set; }

    /// <summary>Размах шага, 0–1: стоит — 0, идёт со скоростью <see cref="FullSwingSpeed"/> и быстрее — 1.</summary>
    public float PreviousSwing { get; set; }
    public float CurrentSwing { get; set; }

    /// <summary>Когда сущность в последний раз получила урон — по часам кадра клиента, секунды; для вспышки (шаг 9.4).</summary>
    public double HurtAt { get; set; } = double.NegativeInfinity;

    /// <summary>Положение между двумя последними тиками: <paramref name="alpha"/> 0 — прошлый тик, 1 — последний.</summary>
    public Vector3d At(float alpha) => Previous + (Current - Previous) * alpha;

    /// <summary>Поворот между двумя последними тиками — по кратчайшей дуге: с 350° на 10° — через 0°, а не назад через 180°.</summary>
    public float YawAt(float alpha)
    {
        var delta = MathF.IEEERemainder(CurrentYaw - PreviousYaw, MathF.Tau);
        return PreviousYaw + delta * alpha;
    }

    /// <summary>Поворот головы относительно тела между тиками (он от −π до π, кратчайшая дуга не нужна).</summary>
    public float HeadTurnAt(float alpha) => PreviousHeadTurn + (CurrentHeadTurn - PreviousHeadTurn) * alpha;

    public float WalkAt(float alpha) => PreviousWalk + (CurrentWalk - PreviousWalk) * alpha;

    public float SwingAt(float alpha) => PreviousSwing + (CurrentSwing - PreviousSwing) * alpha;

    /// <summary>Конец тика: путь за тик (от прошлого положения к последнему) прибавляется к шагу, размах тянется к скорости.</summary>
    public void UpdateWalk()
    {
        var (dx, dz) = (Current.X - Previous.X, Current.Z - Previous.Z);
        var step = (float)Math.Sqrt(dx * dx + dz * dz);
        CurrentWalk += step;
        var target = Math.Min(1f, step * GameTime.TicksPerSecond / FullSwingSpeed);
        CurrentSwing += (target - CurrentSwing) * SwingSmoothing;
    }
}

/// <summary>
/// Сущности, которые сервер показал клиенту (<see cref="EntitySpawned"/> и следующие за ним сообщения). Клиент ничего
/// не решает — только помнит и плавно рисует: положение между тиками интерполируется, как камера игрока.
/// </summary>
internal sealed class ClientEntities
{
    private readonly Dictionary<EntityId, ClientEntity> _entities = [];

    public int Count => _entities.Count;

    public IEnumerable<KeyValuePair<EntityId, ClientEntity>> All => _entities;

    /// <summary>Начало тика клиента, до сообщений сервера: последнее положение становится прошлым.</summary>
    public void BeginTick()
    {
        foreach (var entity in _entities.Values)
        {
            entity.Previous = entity.Current;
            entity.PreviousYaw = entity.CurrentYaw;
            entity.PreviousHeadTurn = entity.CurrentHeadTurn;
            entity.PreviousWalk = entity.CurrentWalk;
            entity.PreviousSwing = entity.CurrentSwing;
        }
    }

    /// <summary>Конец тика клиента, после сообщений сервера: шаг мобов по пути за этот тик (<see cref="ClientEntity.UpdateWalk"/>).</summary>
    public void EndTick()
    {
        foreach (var entity in _entities.Values)
        {
            if (entity.Kind == EntityKind.Mob)
                entity.UpdateWalk();
        }
    }

    /// <summary>Применяет сообщение о сущностях. Возвращает false, если оно не о них.</summary>
    public bool Apply(ServerMessage message)
    {
        switch (message)
        {
            case EntitySpawned spawned:
                // Повторное появление (сервер забыл и снова показал) — просто новое состояние, без скачка из старого места.
                _entities[spawned.Entity] = new ClientEntity(spawned.Kind, spawned.Item, spawned.Position, spawned.Mob, spawned.Yaw,
                    spawned.HeadTurn);
                return true;

            case EntitiesMoved moved:
                foreach (var (entity, position, yaw, headTurn) in moved.Entities)
                {
                    if (_entities.TryGetValue(entity, out var known))
                    {
                        known.Current = position;
                        known.CurrentYaw = yaw;
                        known.CurrentHeadTurn = headTurn;
                    }
                }
                return true;

            case EntityRemoved removed:
                _entities.Remove(removed.Entity);
                return true;

            default:
                return false;
        }
    }

    public bool TryGet(EntityId entity, out ClientEntity value) => _entities.TryGetValue(entity, out value!);

    /// <summary>
    /// Ближайший моб, в короб тела которого попадает луч не дальше <paramref name="maxDistance"/>: тот, что нарисован
    /// в этом кадре (положение — по доле тика <paramref name="alpha"/>). Неизвестный реестру вид не выделяется.
    /// </summary>
    public (EntityId Entity, ClientEntity Value, double Distance)? RaycastMob(MobRegistry mobs, Vector3d origin, System.Numerics.Vector3 direction,
        float maxDistance, float alpha)
    {
        (EntityId, ClientEntity, double)? best = null;
        foreach (var (id, entity) in _entities)
        {
            if (entity.Kind != EntityKind.Mob || !mobs.Contains(entity.Mob))
                continue;
            var mob = mobs.Get(entity.Mob);
            if (VoxelRaycast.Distance(BoxPhysics.BodyAt(entity.At(alpha), mob.Width, mob.Height), origin, direction) is { } distance
                && distance <= maxDistance && (best is null || distance < best.Value.Item3))
                best = (id, entity, distance);
        }
        return best;
    }
}
