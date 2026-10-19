using Server.Storage;
using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Voxels;

namespace Server.Entities;

/// <summary>
/// Сущности в сохранении — вместе со своими чанками: чанк выгружен — его сущности записаны и убраны из мира; чанк
/// загружен — его сущности снова в мире (<see cref="Restore"/>). При автосохранении и выходе записываются все.
/// Без своего чанка сущность не живёт: брошенное у дома лежит там и после перезапуска, но не держит память, пока
/// игрок далеко.
/// </summary>
/// <remarks>Чей чанк — по точке сущности в момент записи. Мир без сохранения (тесты) сущности выгруженных чанков просто забывает.</remarks>
internal sealed class EntityPersistence(EntityWorld entities, WorldSave? save, ItemRegistry items)
{
    /// <summary>Чанки, у которых в сохранении есть строка сущностей: опустевший такой чанк нужно стереть из сохранения.</summary>
    private readonly HashSet<ChunkPos> _stored = [];

    private readonly Dictionary<ChunkPos, List<(EntityId Entity, SavedItem Item)>> _byChunk = [];

    /// <summary>Возвращает в мир сущности чанка, прочитанные из сохранения. Испорченные данные — строка в лог.</summary>
    public void Restore(ChunkPos chunk, byte[] data)
    {
        if (save == null)
            return;
        // Строка есть: даже если её не прочитать, при следующей записи чанка она будет перезаписана или стёрта.
        _stored.Add(chunk);

        // В чанке уже есть живые сущности: они были в памяти всё время, пока чанка не было (созданы в незагруженном
        // месте), и автосохранение записало их же. Правда — в памяти; восстановить строку значило бы удвоить их.
        if (HasLiveEntitiesIn(chunk))
            return;

        List<SavedItem> saved;
        var skipped = new List<string>();
        try
        {
            saved = EntityCodec.Decode(data, save.ItemIds, items, skipped);
        }
        catch (InvalidDataException e)
        {
            Console.WriteLine($"[Server] Сущности чанка {chunk} в сохранении испорчены, они пропадут: {e.Message}");
            return;
        }
        if (skipped.Count > 0)
            Console.WriteLine($"[Server] В чанке {chunk} пропущены сущности неизвестных видов: {string.Join(", ", skipped.Distinct())}");

        foreach (var item in saved)
            ItemEntities.Restore(entities, item);
    }

    /// <summary>
    /// Записывает сущности чанков <paramref name="chunks"/>; <paramref name="remove"/> — заодно убирает их из мира
    /// (чанки выгружены).
    /// </summary>
    public void Save(IReadOnlyCollection<ChunkPos> chunks, bool remove)
    {
        if (chunks.Count == 0)
            return;
        var wanted = chunks as IReadOnlySet<ChunkPos> ?? chunks.ToHashSet();
        Group(wanted);

        foreach (var chunk in chunks)
        {
            var group = _byChunk.GetValueOrDefault(chunk);
            if (save != null)
            {
                if (group is { Count: > 0 })
                {
                    save.SaveEntities(chunk, EntityCodec.Encode([.. group.Select(g => g.Item)], save.ItemIds));
                    _stored.Add(chunk);
                }
                else if (_stored.Remove(chunk))
                {
                    save.SaveEntities(chunk, null);
                }
            }

            if (remove && group != null)
            {
                foreach (var (entity, _) in group)
                    entities.Destroy(entity);
            }
        }
    }

    /// <summary>Записывает все сущности (автосохранение, выход) и стирает опустевшие чанки.</summary>
    public void SaveAll()
    {
        Group(null);
        var chunks = _byChunk.Keys.Concat(_stored).ToHashSet();
        Save(chunks, remove: false);
    }

    private bool HasLiveEntitiesIn(ChunkPos chunk)
    {
        foreach (var entity in entities.Query<DroppedItem, Position>())
        {
            if (BlockPos.Containing(entities.Get<Position>(entity).Value).Chunk == chunk)
                return true;
        }
        return false;
    }

    /// <summary>Сущности по чанкам; <paramref name="only"/> — только эти чанки.</summary>
    private void Group(IReadOnlySet<ChunkPos>? only)
    {
        _byChunk.Clear();
        foreach (var entity in entities.Query<DroppedItem, Position>())
        {
            var position = entities.Get<Position>(entity).Value;
            var chunk = BlockPos.Containing(position).Chunk;
            if (only != null && !only.Contains(chunk))
                continue;

            var item = entities.Get<DroppedItem>(entity);
            var body = entities.Has<Body>(entity) ? entities.Get<Body>(entity) : default;
            if (!_byChunk.TryGetValue(chunk, out var group))
                _byChunk[chunk] = group = [];
            group.Add((entity, new SavedItem(position, body.Velocity, body.OnGround, item.Stack, item.PickupDelay, item.Age)));
        }
    }
}
