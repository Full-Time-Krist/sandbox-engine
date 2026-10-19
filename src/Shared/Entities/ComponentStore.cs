namespace Shared.Entities;

/// <summary>Хранилище компонентов одного типа без знания типа — чтобы мир сущностей мог чистить и опрашивать все сразу.</summary>
internal interface IComponentStore
{
    int Count { get; }
    ReadOnlySpan<int> Entities { get; }
    bool Has(int entity);
    bool Remove(int entity);
}

/// <summary>
/// Компоненты одного типа по индексам сущностей — «разреженное множество» (sparse set).
/// </summary>
/// <remarks>
/// Значения лежат плотно, подряд в массиве, без дыр: обход всех компонентов типа — проход по памяти подряд, без
/// словарей и указателей. Разреженный массив по индексу сущности хранит место значения в плотном (плюс один: 0 —
/// компонента нет), поэтому «есть ли», «взять» и «удалить» — за постоянное время. Удаление переносит последнее
/// значение на место удалённого, порядок значений не сохраняется.
/// <para>
/// Ссылка из <see cref="Get"/> действует, пока в это хранилище не добавили и не удалили компонент: добавление может
/// переложить массив, удаление — перенести на это место другое значение.
/// </para>
/// </remarks>
public sealed class ComponentStore<T> : IComponentStore
{
    private int[] _sparse = [];
    private int[] _entities = new int[16];
    private T[] _values = new T[16];

    public int Count { get; private set; }

    /// <summary>Индексы сущностей, у которых есть компонент, в порядке плотного массива.</summary>
    public ReadOnlySpan<int> Entities => _entities.AsSpan(0, Count);

    public bool Has(int entity) => (uint)entity < (uint)_sparse.Length && _sparse[entity] != 0;

    public ref T Get(int entity)
    {
        if (!Has(entity))
            throw new KeyNotFoundException($"У сущности с индексом {entity} нет компонента {typeof(T).Name}.");
        return ref _values[_sparse[entity] - 1];
    }

    /// <summary>Добавляет компонент или заменяет имеющийся.</summary>
    public void Set(int entity, in T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entity);
        if (Has(entity))
        {
            _values[_sparse[entity] - 1] = value;
            return;
        }

        if (entity >= _sparse.Length)
            Array.Resize(ref _sparse, Math.Max(entity + 1, _sparse.Length * 2));
        if (Count == _values.Length)
        {
            Array.Resize(ref _values, Count * 2);
            Array.Resize(ref _entities, Count * 2);
        }

        _values[Count] = value;
        _entities[Count] = entity;
        _sparse[entity] = ++Count;
    }

    public bool Remove(int entity)
    {
        if (!Has(entity))
            return false;

        var slot = _sparse[entity] - 1;
        var last = Count - 1;
        if (slot != last)
        {
            _values[slot] = _values[last];
            _entities[slot] = _entities[last];
            _sparse[_entities[slot]] = slot + 1;
        }
        _values[last] = default!; // не держать ссылки удалённого компонента для сборщика мусора
        _sparse[entity] = 0;
        Count = last;
        return true;
    }
}
