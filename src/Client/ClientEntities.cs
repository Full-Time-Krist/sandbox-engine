using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Protocol;

namespace Client;

/// <summary>Сущность глазами клиента: что рисовать и где она была в двух последних тиках.</summary>
internal sealed class ClientEntity(EntityKind kind, ItemStack item, Vector3d position)
{
    public EntityKind Kind { get; } = kind;
    public ItemStack Item { get; } = item;
    public Vector3d Previous { get; set; } = position;
    public Vector3d Current { get; set; } = position;

    /// <summary>Положение между двумя последними тиками: <paramref name="alpha"/> 0 — прошлый тик, 1 — последний.</summary>
    public Vector3d At(float alpha) => Previous + (Current - Previous) * alpha;
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
            entity.Previous = entity.Current;
    }

    /// <summary>Применяет сообщение о сущностях. Возвращает false, если оно не о них.</summary>
    public bool Apply(ServerMessage message)
    {
        switch (message)
        {
            case EntitySpawned spawned:
                // Повторное появление (сервер забыл и снова показал) — просто новое состояние, без скачка из старого места.
                _entities[spawned.Entity] = new ClientEntity(spawned.Kind, spawned.Item, spawned.Position);
                return true;

            case EntitiesMoved moved:
                foreach (var (entity, position) in moved.Entities)
                {
                    if (_entities.TryGetValue(entity, out var known))
                        known.Current = position;
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
