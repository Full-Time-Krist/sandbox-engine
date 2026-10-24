using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Protocol;

namespace Client;

/// <summary>Клиентские снимки сущностей: только отображение, без игровой логики.</summary>
internal sealed class ClientEntities
{
    private readonly Dictionary<EntityId, ClientEntity> _entities = [];
    public IEnumerable<ClientEntity> All => _entities.Values;
    public int Count => _entities.Count;
    public void BeginTick()
    {
        foreach (var entity in _entities.Values) entity.Previous = entity.Position;
    }
    public void Spawn(EntitySpawned spawn)
    {
        if (_entities.TryGetValue(spawn.Id, out var entity))
        {
            entity.Stack = spawn.Stack;
            entity.Position = spawn.Position;
        }
        else _entities[spawn.Id] = new(spawn.Id, spawn.Position, spawn.Stack);
    }
    public void Move(EntitiesMoved moved)
    {
        foreach (var movement in moved.Entities)
            if (_entities.TryGetValue(movement.Id, out var entity)) entity.Position = movement.Position;
    }
    public void Remove(EntityId id) => _entities.Remove(id);
}

internal sealed class ClientEntity(EntityId id, Vector3d position, ItemStack stack)
{
    public EntityId Id { get; } = id;
    public ItemStack Stack { get; set; } = stack;
    public Vector3d Previous { get; set; } = position;
    public Vector3d Position { get; set; } = position;
    public Vector3d PositionAt(float alpha) => Vector3d.Lerp(Previous, Position, Math.Clamp(alpha, 0, 1));
}
