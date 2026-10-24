namespace Shared.Entities;

/// <summary>Сущности и компоненты. Принадлежит одному потоку сервера.</summary>
public sealed class EntityWorld
{
    private readonly List<uint> _generations = [];
    private readonly List<bool> _alive = [];
    private readonly Stack<int> _free = [];
    private readonly HashSet<EntityId> _destroyed = [];
    private readonly Dictionary<Type, IComponentStore> _stores = [];

    public int Count { get; private set; }

    public EntityId Create()
    {
        int index;
        if (_free.TryPop(out index)) _alive[index] = true;
        else
        {
            index = _generations.Count;
            _generations.Add(1);
            _alive.Add(true);
        }
        Count++;
        return new EntityId(index, _generations[index]);
    }

    public bool IsAlive(EntityId entity) =>
        (uint)entity.Index < (uint)_generations.Count && _alive[entity.Index] &&
        _generations[entity.Index] == entity.Generation;

    /// <summary>Ставит удаление в очередь. До конца тика сущность остаётся доступной всем системам.</summary>
    public void Destroy(EntityId entity)
    {
        if (IsAlive(entity)) _destroyed.Add(entity);
    }

    /// <summary>Вызывается один раз после всех систем тика; не вызывать во время обхода запросов.</summary>
    public void FlushDestroyed()
    {
        foreach (var entity in _destroyed)
        {
            foreach (var store in _stores.Values) store.Remove(entity);
            _alive[entity.Index] = false;
            // После переполнения поколений слот больше не переиспользуется: старый номер не оживёт.
            if (_generations[entity.Index] != uint.MaxValue)
            {
                _generations[entity.Index]++;
                _free.Push(entity.Index);
            }
            Count--;
        }
        _destroyed.Clear();
    }

    public void Set<T>(EntityId entity, T component) where T : struct
    {
        RequireAlive(entity);
        if (FindStore<T>() is not { } store)
        {
            store = new ComponentStore<T>();
            _stores.Add(typeof(T), store);
        }
        store.Set(entity, component);
    }

    /// <summary>Не сохранять ссылку через структурные изменения хранилища компонентов.</summary>
    public ref T Get<T>(EntityId entity) where T : struct
    {
        RequireAlive(entity);
        var store = FindStore<T>() ?? throw new KeyNotFoundException($"Нет компонентов {typeof(T).Name}.");
        return ref store.Get(entity);
    }

    public bool Has<T>(EntityId entity) where T : struct => IsAlive(entity) && FindStore<T>() is { } store && store.Has(entity);
    public bool Remove<T>(EntityId entity) where T : struct => IsAlive(entity) && FindStore<T>() is { } store && store.Remove(entity);

    /// <summary>Обход плотного массива. Во время обхода допустим Destroy, структурные изменения — между системами.</summary>
    public IEnumerable<EntityId> Query<T>() where T : struct
    {
        if (FindStore<T>() is not { } store) yield break;
        for (int i = 0; i < store.Count; i++) yield return store.EntityAt(i);
    }

    public IEnumerable<EntityId> Query<T1, T2>() where T1 : struct where T2 : struct
    {
        var first = FindStore<T1>();
        var second = FindStore<T2>();
        if (first is null || second is null) yield break;
        IComponentStore smallest = first.Count <= second.Count ? first : second;
        for (int i = 0; i < smallest.Count; i++)
        {
            var entity = smallest.EntityAt(i);
            if (first.Has(entity) && second.Has(entity)) yield return entity;
        }
    }

    public IEnumerable<EntityId> Query<T1, T2, T3>() where T1 : struct where T2 : struct where T3 : struct
    {
        var first = FindStore<T1>();
        var second = FindStore<T2>();
        var third = FindStore<T3>();
        if (first is null || second is null || third is null) yield break;
        IComponentStore smallest = first.Count <= second.Count ? first : second;
        if (third.Count < smallest.Count) smallest = third;
        for (int i = 0; i < smallest.Count; i++)
        {
            var entity = smallest.EntityAt(i);
            if (first.Has(entity) && second.Has(entity) && third.Has(entity)) yield return entity;
        }
    }

    private ComponentStore<T>? FindStore<T>() where T : struct =>
        _stores.TryGetValue(typeof(T), out var store) ? (ComponentStore<T>)store : null;

    private void RequireAlive(EntityId entity)
    {
        if (!IsAlive(entity)) throw new InvalidOperationException($"Сущность {entity} не существует.");
    }
}
