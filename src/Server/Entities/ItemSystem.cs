using System.Numerics;
using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Physics;

namespace Server.Entities;

/// <summary>
/// Жизнь предметов на земле: старение и исчезновение, притяжение к игроку, подбор, слияние одинаковых. Методы
/// вызываются в тике сервера по порядку: <see cref="Age"/> → <see cref="Attract"/> → движение → <see cref="PickUp"/> →
/// <see cref="Merge"/>. Ни один не создаёт и не теряет предметы: подобранное ушло в инвентарь, слитое — в соседнюю стопку.
/// </summary>
/// <remarks>Подбирают и притягивают только игроки в выживании: в творческом предметы бесконечные.</remarks>
internal sealed class ItemSystem(ItemRegistry items)
{
    /// <summary>С какого расстояния (от тела игрока до предмета) предмет летит к игроку.</summary>
    public const float AttractRadius = 3f;

    /// <summary>Скорость полёта к игроку, блоков в секунду.</summary>
    public const float AttractSpeed = 8f;

    /// <summary>Насколько тело игрока «шире» для подбора: предмету достаточно оказаться почти вплотную.</summary>
    public const double PickupReach = 0.3;

    /// <summary>Одинаковые предметы ближе этого сливаются в одну стопку (до предела стопки).</summary>
    public const double MergeRadius = 0.5;

    private static readonly Vector3d BodyCenter = new(0, PlayerPhysics.Height / 2, 0);
    private static readonly Vector3d ItemCenter = new(0, ItemEntities.Shape.Height / 2, 0);

    private readonly List<(EntityId Entity, Vector3d Position)> _byX = [];

    /// <summary>Тикает задержку подбора и возраст; проживший <see cref="ItemEntities.LifetimeTicks"/> исчезает.</summary>
    public void Age(EntityWorld entities)
    {
        foreach (var entity in entities.Query<DroppedItem>())
        {
            ref var item = ref entities.Get<DroppedItem>(entity);
            if (item.PickupDelay > 0)
                item.PickupDelay--;
            if (++item.Age >= ItemEntities.LifetimeTicks)
                entities.Destroy(entity);
        }
    }

    /// <summary>
    /// Предмет, который уже можно подобрать, летит к ближайшему игроку в выживании в радиусе <see cref="AttractRadius"/>,
    /// если тому есть куда его положить. Скорость задаётся до движения: столкновения с блоками остаются в силе.
    /// </summary>
    public void Attract(EntityWorld entities, IEnumerable<ClientSession> sessions)
    {
        foreach (var entity in entities.Query<DroppedItem, Position, Body>())
        {
            var item = entities.Get<DroppedItem>(entity);
            if (item.PickupDelay > 0)
                continue;

            var center = entities.Get<Position>(entity).Value + ItemCenter;
            ClientSession? nearest = null;
            var nearestDistance = double.MaxValue;
            foreach (var session in sessions)
            {
                if (!CanPickUp(session, item.Stack))
                    continue;
                var distance = (session.PlayerPosition + BodyCenter - center).Length();
                if (distance <= AttractRadius + PlayerPhysics.Height / 2 && distance < nearestDistance)
                    (nearest, nearestDistance) = (session, distance);
            }
            if (nearest == null || nearestDistance < 1e-6)
                continue;

            var direction = ((nearest.PlayerPosition + BodyCenter - center) * (1 / nearestDistance)).ToVector3();
            entities.Get<Body>(entity).Velocity = direction * AttractSpeed;
        }
    }

    /// <summary>
    /// Предмет, коснувшийся тела игрока в выживании, уходит в его инвентарь, сколько поместится (доливая стопки, как
    /// добыча); остаток лежит дальше. <paramref name="pickedUp"/> — для события модов: кто и сколько подобрал.
    /// </summary>
    public void PickUp(EntityWorld entities, IEnumerable<ClientSession> sessions, Action<ClientSession, ItemStack> pickedUp)
    {
        foreach (var entity in entities.Query<DroppedItem, Position>())
        {
            ref var item = ref entities.Get<DroppedItem>(entity);
            if (item.PickupDelay > 0)
                continue;

            var box = BoxPhysics.BodyAt(entities.Get<Position>(entity).Value, ItemEntities.Shape.Width, ItemEntities.Shape.Height);
            foreach (var session in sessions)
            {
                if (!CanPickUp(session, item.Stack) || !Reach(session).Intersects(box))
                    continue;

                var left = session.Inventory.Add(item.Stack);
                var taken = item.Stack.Count - left;
                if (taken == 0)
                    continue;
                pickedUp(session, new ItemStack(item.Stack.Item, taken));
                if (left == 0)
                {
                    entities.Destroy(entity);
                    break;
                }
                item.Stack = new ItemStack(item.Stack.Item, left);
            }
        }
    }

    /// <summary>
    /// Одинаковые предметы ближе <see cref="MergeRadius"/> сливаются: стопка переливается в соседнюю до предела, пустая
    /// исчезает. Пары ищутся по списку, упорядоченному по X: сравниваются только соседи в полосе шириной радиуса.
    /// </summary>
    public void Merge(EntityWorld entities)
    {
        _byX.Clear();
        foreach (var entity in entities.Query<DroppedItem, Position>())
        {
            var stack = entities.Get<DroppedItem>(entity).Stack;
            if (stack.Count < items.Get(stack.Item).MaxStackSize)
                _byX.Add((entity, entities.Get<Position>(entity).Value));
        }
        _byX.Sort((a, b) => a.Position.X.CompareTo(b.Position.X));

        for (var i = 0; i < _byX.Count; i++)
        {
            var (target, at) = _byX[i];
            for (var j = i + 1; j < _byX.Count && _byX[j].Position.X - at.X <= MergeRadius; j++)
            {
                if (!entities.IsAlive(target))
                    break;
                var (source, from) = _byX[j];
                if (!entities.IsAlive(source) || (from - at).Length() > MergeRadius)
                    continue;
                MergeInto(entities, target, source);
            }
        }
    }

    private void MergeInto(EntityWorld entities, EntityId target, EntityId source)
    {
        ref var into = ref entities.Get<DroppedItem>(target);
        ref var from = ref entities.Get<DroppedItem>(source);
        if (into.Stack.Item != from.Stack.Item)
            return;

        var moved = Math.Min(from.Stack.Count, items.Get(into.Stack.Item).MaxStackSize - into.Stack.Count);
        if (moved <= 0)
            return;

        into.Stack = new ItemStack(into.Stack.Item, into.Stack.Count + moved);
        // Слитый живёт по младшему из двух и ждёт подбора по большей задержке: слияние не ускоряет ни то, ни другое.
        into.Age = Math.Min(into.Age, from.Age);
        into.PickupDelay = Math.Max(into.PickupDelay, from.PickupDelay);
        if (moved == from.Stack.Count)
            entities.Destroy(source);
        else
            from.Stack = new ItemStack(from.Stack.Item, from.Stack.Count - moved);
    }

    private static bool CanPickUp(ClientSession session, ItemStack stack) =>
        session.IsJoined && session.Mode == GameMode.Survival && session.Inventory.CanAdd(new ItemStack(stack.Item, 1));

    private static Aabb Reach(ClientSession session)
    {
        var body = PlayerPhysics.BodyAt(session.PlayerPosition);
        var grow = new Vector3d(PickupReach, PickupReach, PickupReach);
        return new Aabb(body.Min - grow, body.Max + grow);
    }
}
