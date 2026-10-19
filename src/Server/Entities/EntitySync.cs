using Shared;
using Shared.Entities;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Entities;

/// <summary>
/// Рассылка сущностей клиентам. Клиент знает сущность, пока её чанк у него есть (как с <see cref="BlockChanged"/>):
/// появилась в таком чанке или клиент получил её чанк — <see cref="EntitySpawned"/>; сдвинулась — в общем
/// <see cref="EntitiesMoved"/> за тик; удалена или её чанк выгружен у клиента — <see cref="EntityRemoved"/>.
/// </summary>
/// <remarks>
/// Вызывается после рассылки чанков: клиент сначала получает чанк, потом его сущности. Список известных клиенту
/// сущностей — <see cref="ClientSession.KnownEntities"/>. Буферы общие для всех сессий, поэтому экземпляр — на сервер.
/// </remarks>
internal sealed class EntitySync
{
    private readonly List<EntityId> _removed = [];
    private readonly List<EntityPosition> _moved = [];

    public void Run(EntityWorld entities, IEnumerable<ClientSession> sessions)
    {
        foreach (var session in sessions)
        {
            if (session.IsJoined)
                SyncSession(entities, session);
        }

        foreach (var entity in entities.Query<Position, Synced>())
            entities.Get<Synced>(entity).SentPosition = entities.Get<Position>(entity).Value;
    }

    private void SyncSession(EntityWorld entities, ClientSession session)
    {
        var known = session.KnownEntities;

        _removed.Clear();
        foreach (var entity in known)
        {
            if (!IsVisible(entities, session, entity))
                _removed.Add(entity);
        }
        foreach (var entity in _removed)
        {
            known.Remove(entity);
            session.Connection.Send(new EntityRemoved(entity));
        }

        _moved.Clear();
        foreach (var entity in entities.Query<Position, DroppedItem>())
        {
            var position = entities.Get<Position>(entity).Value;
            if (!session.View.WasSent(ChunkOf(position)))
                continue;
            if (known.Add(entity))
                session.Connection.Send(new EntitySpawned(entity, EntityKind.Item, position, entities.Get<DroppedItem>(entity).Stack));
            else if (entities.Get<Synced>(entity).SentPosition != position)
                _moved.Add(new EntityPosition(entity, position));
        }
        if (_moved.Count > 0)
            session.Connection.Send(new EntitiesMoved([.. _moved]));
    }

    private static bool IsVisible(EntityWorld entities, ClientSession session, EntityId entity) =>
        entities.IsAlive(entity) && entities.Has<Position>(entity)
        && session.View.WasSent(ChunkOf(entities.Get<Position>(entity).Value));

    private static ChunkPos ChunkOf(Vector3d position) => BlockPos.Containing(position).Chunk;
}
