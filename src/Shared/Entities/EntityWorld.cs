using System.Buffers;

namespace Shared.Entities;

/// <summary>
/// Сущности и их компоненты (ECS): сущность — только номер, данные — компоненты (обычные структуры), поведение —
/// системы, которые обходят сущности с нужным набором компонентов (<see cref="Query{T1}"/>).
/// </summary>
/// <remarks>
/// Удаление отложенное. <see cref="Destroy"/> сразу делает сущность мёртвой (её не видят запросы, <see cref="IsAlive"/>
/// ложно), а компоненты убирает и индекс освобождает <see cref="FlushDestroyed"/> в конце тика. Так номер не
/// переиспользуется посреди тика, и удаление во время обхода не сдвигает обходимые массивы.
/// <para>Не потокобезопасен: живёт в потоке сервера, как мир блоков.</para>
/// </remarks>
public sealed class EntityWorld
{
    private int[] _generations = new int[64];
    private bool[] _alive = new bool[64];
    private int _indexCount;
    private readonly Stack<int> _free = new();
    private readonly List<int> _destroyed = [];
    private IComponentStore?[] _stores = new IComponentStore?[8];

    /// <summary>Число живых сущностей (удалённые в этом тике уже не считаются).</summary>
    public int Count { get; private set; }

    public EntityId Create()
    {
        if (!_free.TryPop(out var index))
        {
            index = _indexCount++;
            if (index == _generations.Length)
            {
                Array.Resize(ref _generations, index * 2);
                Array.Resize(ref _alive, index * 2);
            }
            _generations[index] = 1;
        }

        _alive[index] = true;
        Count++;
        return new EntityId(index, _generations[index]);
    }

    public bool IsAlive(EntityId id) =>
        (uint)id.Index < (uint)_indexCount && _alive[id.Index] && _generations[id.Index] == id.Generation;

    /// <summary>Удаляет сущность; компоненты уходят в <see cref="FlushDestroyed"/>. Мёртвый или чужой номер — <c>false</c>.</summary>
    public bool Destroy(EntityId id)
    {
        if (!IsAlive(id))
            return false;
        _alive[id.Index] = false;
        _destroyed.Add(id.Index);
        Count--;
        return true;
    }

    /// <summary>Убирает компоненты удалённых сущностей и освобождает их индексы с новым поколением. Конец тика.</summary>
    public void FlushDestroyed()
    {
        foreach (var index in _destroyed)
        {
            foreach (var store in _stores)
                store?.Remove(index);
            _generations[index]++;
            _free.Push(index);
        }
        _destroyed.Clear();
    }

    /// <summary>Добавляет компонент или заменяет имеющийся.</summary>
    public void Set<T>(EntityId id, in T component)
    {
        RequireAlive(id);
        Store<T>().Set(id.Index, component);
    }

    public bool Has<T>(EntityId id) => IsAlive(id) && FindStore<T>() is { } store && store.Has(id.Index);

    /// <summary>Ссылка на компонент — менять его на месте. Действует до следующего добавления или удаления компонента этого типа.</summary>
    public ref T Get<T>(EntityId id)
    {
        RequireAlive(id);
        return ref Store<T>().Get(id.Index);
    }

    public bool Remove<T>(EntityId id) => IsAlive(id) && FindStore<T>() is { } store && store.Remove(id.Index);

    /// <summary>Живые сущности с компонентом <typeparamref name="T1"/>.</summary>
    public EntityQuery Query<T1>() => new(this, FindStore<T1>(), null, null, 1);

    /// <summary>Живые сущности, у которых есть оба компонента.</summary>
    public EntityQuery Query<T1, T2>() => new(this, FindStore<T1>(), FindStore<T2>(), null, 2);

    /// <summary>Живые сущности, у которых есть все три компонента.</summary>
    public EntityQuery Query<T1, T2, T3>() => new(this, FindStore<T1>(), FindStore<T2>(), FindStore<T3>(), 3);

    internal EntityId AliveId(int index) =>
        _alive[index] ? new EntityId(index, _generations[index]) : EntityId.None;

    private void RequireAlive(EntityId id)
    {
        if (!IsAlive(id))
            throw new InvalidOperationException($"Сущность {id} не существует или удалена.");
    }

    private ComponentStore<T>? FindStore<T>()
    {
        var type = ComponentType<T>.Id;
        return type < _stores.Length ? (ComponentStore<T>?)_stores[type] : null;
    }

    private ComponentStore<T> Store<T>()
    {
        var type = ComponentType<T>.Id;
        if (type >= _stores.Length)
            Array.Resize(ref _stores, Math.Max(type + 1, _stores.Length * 2));
        return (ComponentStore<T>)(_stores[type] ??= new ComponentStore<T>());
    }

    /// <summary>Номер типа компонента — индекс хранилища в массиве, без словаря на каждое обращение.</summary>
    private static class ComponentType<T>
    {
        public static readonly int Id = Interlocked.Increment(ref _nextComponentType) - 1;
    }

    private static int _nextComponentType;
}

/// <summary>
/// Запрос сущностей по компонентам — для <c>foreach</c>. Компоненты берутся через <see cref="EntityWorld.Get{T}"/>.
/// </summary>
/// <remarks>
/// Обход идёт по снимку сущностей самого маленького из хранилищ, снятому в начале <c>foreach</c>, и каждая сущность
/// перед выдачей проверяется заново. Поэтому внутри обхода можно удалять сущности и компоненты (удалённое пропускается,
/// ничего не выдаётся дважды) и создавать новые (они в этот обход не попадут).
/// </remarks>
public readonly struct EntityQuery
{
    private readonly EntityWorld _world;
    private readonly IComponentStore? _a, _b, _c;
    private readonly bool _empty;

    internal EntityQuery(EntityWorld world, IComponentStore? a, IComponentStore? b, IComponentStore? c, int arity)
    {
        _world = world;
        (_a, _b, _c) = (a, b, c);
        // Хранилища нет — компонент ни разу не добавляли, сущностей с ним нет.
        _empty = a == null || (arity >= 2 && b == null) || (arity >= 3 && c == null);
    }

    public Enumerator GetEnumerator() => new(_world, _empty ? null : _a, _b, _c);

    public struct Enumerator : IDisposable
    {
        private readonly EntityWorld _world;
        private readonly IComponentStore? _a, _b, _c;
        private int[]? _snapshot;
        private readonly int _length;
        private int _position;

        internal Enumerator(EntityWorld world, IComponentStore? a, IComponentStore? b, IComponentStore? c)
        {
            _world = world;
            (_a, _b, _c) = (a, b, c);
            _position = -1;
            Current = EntityId.None;
            if (a == null)
                return;

            var smallest = a;
            if (b != null && b.Count < smallest.Count)
                smallest = b;
            if (c != null && c.Count < smallest.Count)
                smallest = c;
            _length = smallest.Count;
            _snapshot = ArrayPool<int>.Shared.Rent(_length);
            smallest.Entities.CopyTo(_snapshot);
        }

        public EntityId Current { get; private set; }

        public bool MoveNext()
        {
            while (++_position < _length)
            {
                var index = _snapshot![_position];
                var id = _world.AliveId(index);
                if (!id.IsNone && _a!.Has(index) && (_b == null || _b.Has(index)) && (_c == null || _c.Has(index)))
                {
                    Current = id;
                    return true;
                }
            }
            return false;
        }

        public void Dispose()
        {
            if (_snapshot != null)
                ArrayPool<int>.Shared.Return(_snapshot);
            _snapshot = null;
        }
    }
}
