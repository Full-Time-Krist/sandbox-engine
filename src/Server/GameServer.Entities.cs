using System.Collections.Immutable;
using System.Numerics;
using Api;
using Server.Storage;
using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server;

public sealed partial class GameServer
{
    internal EntityWorld Entities { get; } = new();
    private readonly Dictionary<ClientSession, Dictionary<EntityId, (Vector3d Position, ItemStack Stack)>> _knownEntities = [];
    private readonly HashSet<ChunkPos> _entityChunks = [];
    private readonly Dictionary<(int X, int Y, int Z), List<EntityId>> _mergeCells = [];
    private const int ItemLifetime = 5 * 60 * GameTime.TicksPerSecond;

    private record struct GroundItem(ItemStack Stack, Vector3 Velocity, bool OnGround, int Age, int Delay);

    internal IEnumerable<SavedItemEntity> GroundItems => Entities.Query<GroundItem, PositionComponent>().Select(SnapshotItem);

    private SavedItemEntity SnapshotItem(EntityId id)
    {
        var item = Entities.Get<GroundItem>(id);
        return new(Entities.Get<PositionComponent>(id).Value, item.Velocity, item.Stack, item.Age, item.Delay);
    }

    internal EntityId SpawnItem(Vector3d position, ItemStack stack, Vector3d velocity = default,
        int pickupDelay = 10, int ageTicks = 0)
    {
        if (!IsValidPosition(position) || !velocity.IsFinite || velocity.LengthSquared() > 1_000_000 || pickupDelay < 0 || ageTicks < 0 || stack.IsEmpty || !_items.Contains(stack.Item)
            || stack.Count > _items.Get(stack.Item).MaxStackSize || !World.Chunks.Contains(BlockPos.Containing(position).Chunk))
            throw new ArgumentException("Неверный предмет, позиция или чанк предмета не загружен");
        var id = Entities.Create();
        Entities.Set(id, new PositionComponent(position));
        Entities.Set(id, new GroundItem(stack, velocity.ToVector3(), false, ageTicks, pickupDelay));
        return id;
    }

    private void DropSelected(ClientSession session, bool all)
    {
        var stack = session.Inventory.Selected;
        if (stack.IsEmpty) return;
        var count = all ? stack.Count : 1;
        if (ThrowItem(session, new ItemStack(stack.Item, count)))
            session.Inventory.Remove(session.Inventory.SelectedSlot, count);
    }

    private bool ThrowItem(ClientSession session, ItemStack stack)
    {
        var direction = new Vector3(MathF.Sin(session.Look.Yaw) * MathF.Cos(session.Look.Pitch),
            MathF.Sin(session.Look.Pitch), -MathF.Cos(session.Look.Yaw) * MathF.Cos(session.Look.Pitch));
        var position = session.PlayerPosition + new Vector3d(0, 1.2, 0);
        if (!World.Chunks.Contains(BlockPos.Containing(position).Chunk)) return false;
        SpawnItem(position, stack, direction * 5 + new Vector3(0, 2, 0), 2 * GameTime.TicksPerSecond);
        return true;
    }

    private void ReturnCursor(ClientSession session)
    {
        session.Inventory.ReturnCursor();
        if (!session.Inventory.Cursor.IsEmpty && ThrowItem(session, session.Inventory.Cursor))
            session.Inventory.Set(Inventory.CursorSlot, ItemStack.Empty);
    }

    private void TickGroundItems()
    {
        foreach (var id in Entities.Query<GroundItem, PositionComponent>())
        {
            ref var item = ref Entities.Get<GroundItem>(id);
            if (++item.Age >= ItemLifetime)
            {
                Entities.Destroy(id);
                item.Stack = ItemStack.Empty;
                continue;
            }
            if (item.Delay > 0) item.Delay--;
            ref var position = ref Entities.Get<PositionComponent>(id);
            if (item.Delay == 0)
            {
                foreach (var session in _sessions)
                {
                    if (!session.IsJoined || session.IsDead || session.Mode != GameMode.Survival || !session.Inventory.CanAdd(new ItemStack(item.Stack.Item, 1))) continue;
                    var target = session.PlayerPosition + new Vector3d(0, 0.6, 0);
                    var delta = target - position.Value;
                    var distance = delta.Length();
                    if (distance > 3) continue;
                    if (PlayerPhysics.BodyAt(session.PlayerPosition).Stretch(new Vector3d(0.25, 0.25, 0.25)).Intersects(ItemPhysics.BodyAt(position.Value)))
                    {
                        var left = session.Inventory.Add(item.Stack);
                        var picked = item.Stack.Count - left;
                        if (picked > 0)
                            _events.Raise(new ItemPickedUp(_editor, session, new ItemStack(item.Stack.Item, picked)));
                        item.Stack = new ItemStack(item.Stack.Item, left);
                        if (left == 0) { Entities.Destroy(id); break; }
                    }
                    if (distance > 0.001) item.Velocity = (delta * (8 / distance)).ToVector3();
                    break;
                }
            }
            if (item.Stack.IsEmpty) continue;
            var moved = ItemPhysics.Step(new ItemBodyState(position.Value, item.Velocity, item.OnGround), World.Chunks, _registry);
            position.Value = moved.Position;
            item.Velocity = moved.Velocity;
            item.OnGround = moved.OnGround;
        }
        MergeGroundItems();
        Entities.FlushDestroyed();
    }

    private static (int X, int Y, int Z) MergeCell(Vector3d p) => ((int)Math.Floor(p.X * 2), (int)Math.Floor(p.Y * 2), (int)Math.Floor(p.Z * 2));

    private void MergeGroundItems()
    {
        _mergeCells.Clear();
        foreach (var id in Entities.Query<GroundItem, PositionComponent>())
        {
            ref var item = ref Entities.Get<GroundItem>(id);
            if (item.Stack.IsEmpty) continue;
            var position = Entities.Get<PositionComponent>(id).Value;
            var cell = MergeCell(position);
            var max = _items.Get(item.Stack.Item).MaxStackSize;
            for (int x = -1; x <= 1 && !item.Stack.IsEmpty; x++)
            for (int y = -1; y <= 1 && !item.Stack.IsEmpty; y++)
            for (int z = -1; z <= 1 && !item.Stack.IsEmpty; z++)
            {
                if (!_mergeCells.TryGetValue((cell.X + x, cell.Y + y, cell.Z + z), out var neighbors)) continue;
                foreach (var other in neighbors)
                {
                    ref var target = ref Entities.Get<GroundItem>(other);
                    if (target.Stack.Item != item.Stack.Item || target.Stack.Count >= max ||
                        Vector3d.Distance(position, Entities.Get<PositionComponent>(other).Value) > 0.5) continue;
                    var count = Math.Min(item.Stack.Count, max - target.Stack.Count);
                    target.Stack = new(target.Stack.Item, target.Stack.Count + count);
                    target.Delay = Math.Max(target.Delay, item.Delay);
                    target.Age = Math.Max(target.Age, item.Age);
                    item.Stack = new(item.Stack.Item, item.Stack.Count - count);
                    if (item.Stack.IsEmpty) { Entities.Destroy(id); break; }
                }
            }
            if (item.Stack.IsEmpty) continue;
            if (!_mergeCells.TryGetValue(cell, out var list)) _mergeCells[cell] = list = [];
            list.Add(id);
        }
    }

    private void SyncEntities()
    {
        foreach (var session in _sessions)
        {
            if (!session.IsJoined) continue;
            if (!_knownEntities.TryGetValue(session, out var known)) _knownEntities[session] = known = [];
            foreach (var id in known.Keys.ToArray())
            {
                if (!Entities.IsAlive(id) || !session.View.WasSent(BlockPos.Containing(Entities.Get<PositionComponent>(id).Value).Chunk))
                { session.Connection.Send(new EntityRemoved(id)); known.Remove(id); }
            }
            var movements = ImmutableArray.CreateBuilder<EntityMovement>();
            foreach (var id in Entities.Query<GroundItem, PositionComponent>())
            {
                var position = Entities.Get<PositionComponent>(id).Value;
                if (!session.View.WasSent(BlockPos.Containing(position).Chunk)) continue;
                var stack = Entities.Get<GroundItem>(id).Stack;
                if (!known.TryGetValue(id, out var previous) || previous.Stack != stack)
                    session.Connection.Send(new EntitySpawned(id, position, stack));
                else if (previous.Position != position) movements.Add(new EntityMovement(id, position));
                known[id] = (position, stack);
            }
            if (movements.Count > 0) session.Connection.Send(new EntitiesMoved(movements.ToImmutable()));
        }
    }

    private void LoadChunkEntities(ChunkPos chunk)
    {
        if (!_entityChunks.Add(chunk)) return;
        foreach (var item in _save?.LoadEntities(chunk) ?? [])
        {
            if (BlockPos.Containing(item.Position).Chunk != chunk) { Console.WriteLine($"[Server] Предмет вне чанка {chunk}, пропущен"); continue; }
            SpawnItem(item.Position, item.Stack, item.Velocity, item.PickupDelayTicks, item.AgeTicks);
        }
    }

    private void SaveChunkEntities(ChunkPos chunk, bool unload)
    {
        var ids = Entities.Query<GroundItem, PositionComponent>().Where(id => BlockPos.Containing(Entities.Get<PositionComponent>(id).Value).Chunk == chunk).ToArray();
        _save?.SaveEntities(chunk, ids.Select(SnapshotItem).ToArray());
        if (!unload) return;
        foreach (var id in ids) Entities.Destroy(id);
        Entities.FlushDestroyed();
        _entityChunks.Remove(chunk);
    }
}
