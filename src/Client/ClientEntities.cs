using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Mobs;
using Shared.Protocol;

namespace Client;

/// <summary>Сущность глазами клиента: что рисовать и где она была (и куда смотрела) в двух последних тиках.</summary>
internal sealed class ClientEntity(EntityKind kind, ItemStack item, Vector3d position, MobId mob = default, float yaw = 0)
{
    public EntityKind Kind { get; } = kind;
    public ItemStack Item { get; } = item;

    /// <summary>Вид моба (<see cref="EntityKind.Mob"/>).</summary>
    public MobId Mob { get; } = mob;

    public Vector3d Previous { get; set; } = position;
    public Vector3d Current { get; set; } = position;
    public float PreviousYaw { get; set; } = yaw;
    public float CurrentYaw { get; set; } = yaw;

    /// <summary>Положение между двумя последними тиками: <paramref name="alpha"/> 0 — прошлый тик, 1 — последний.</summary>
    public Vector3d At(float alpha) => Previous + (Current - Previous) * alpha;

    /// <summary>Поворот между двумя последними тиками — по кратчайшей дуге: с 350° на 10° — через 0°, а не назад через 180°.</summary>
    public float YawAt(float alpha)
    {
        var delta = MathF.IEEERemainder(CurrentYaw - PreviousYaw, MathF.Tau);
        return PreviousYaw + delta * alpha;
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
        }
    }

    /// <summary>Применяет сообщение о сущностях. Возвращает false, если оно не о них.</summary>
    public bool Apply(ServerMessage message)
    {
        switch (message)
        {
            case EntitySpawned spawned:
                // Повторное появление (сервер забыл и снова показал) — просто новое состояние, без скачка из старого места.
                _entities[spawned.Entity] = new ClientEntity(spawned.Kind, spawned.Item, spawned.Position, spawned.Mob, spawned.Yaw);
                return true;

            case EntitiesMoved moved:
                foreach (var (entity, position, yaw) in moved.Entities)
                {
                    if (_entities.TryGetValue(entity, out var known))
                    {
                        known.Current = position;
                        known.CurrentYaw = yaw;
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
}
