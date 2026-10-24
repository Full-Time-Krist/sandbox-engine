namespace Shared.Entities;

/// <summary>Плотное хранилище компонентов с разреженным индексом по номеру сущности.</summary>
public sealed class ComponentStore<T> : IComponentStore where T : struct
{
    private int[] _sparse = [];
    private EntityId[] _entities = [];
    private T[] _components = [];

    public int Count { get; private set; }

    public bool Has(EntityId entity)
    {
        if ((uint)entity.Index >= (uint)_sparse.Length) return false;
        int slot = _sparse[entity.Index] - 1;
        return (uint)slot < (uint)Count && _entities[slot] == entity;
    }

    public void Set(EntityId entity, T component)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entity.Index);
        if (entity.Generation == 0) throw new ArgumentException("Пустой номер сущности.", nameof(entity));
        EnsureCapacity(ref _sparse, entity.Index + 1);
        if (Has(entity))
        {
            _components[_sparse[entity.Index] - 1] = component;
            return;
        }
        if (_sparse[entity.Index] != 0)
            throw new InvalidOperationException("Слот занят другим поколением сущности.");
        EnsureCapacity(ref _entities, Count + 1);
        EnsureCapacity(ref _components, Count + 1);
        _entities[Count] = entity;
        _components[Count] = component;
        _sparse[entity.Index] = ++Count;
    }

    /// <summary>Ссылка действительна только до следующего добавления или удаления компонента этого типа.</summary>
    public ref T Get(EntityId entity)
    {
        if (!Has(entity)) throw new KeyNotFoundException($"У сущности {entity} нет компонента {typeof(T).Name}.");
        return ref _components[_sparse[entity.Index] - 1];
    }

    public bool Remove(EntityId entity)
    {
        if (!Has(entity)) return false;
        int slot = _sparse[entity.Index] - 1;
        int last = --Count;
        if (slot != last)
        {
            _entities[slot] = _entities[last];
            _components[slot] = _components[last];
            _sparse[_entities[slot].Index] = slot + 1;
        }
        _sparse[entity.Index] = 0;
        _entities[last] = default;
        _components[last] = default;
        return true;
    }

    public EntityId EntityAt(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _entities[index];
    }

    private static void EnsureCapacity<TValue>(ref TValue[] array, int size)
    {
        if (array.Length < size) Array.Resize(ref array, Math.Max(size, Math.Max(8, array.Length * 2)));
    }
}

internal interface IComponentStore
{
    int Count { get; }
    EntityId EntityAt(int index);
    bool Remove(EntityId entity);
}
