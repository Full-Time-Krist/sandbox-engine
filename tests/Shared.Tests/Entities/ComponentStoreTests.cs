using Shared.Entities;

namespace Shared.Tests.Entities;

public class ComponentStoreTests
{
    private readonly ComponentStore<int> _store = new();

    private Dictionary<int, int> Contents()
    {
        var contents = new Dictionary<int, int>();
        foreach (var entity in _store.Entities)
            contents.Add(entity, _store.Get(entity));
        return contents;
    }

    [Fact]
    public void Set_Get_Has()
    {
        _store.Set(5, 50);
        _store.Set(2, 20);

        Assert.True(_store.Has(5));
        Assert.False(_store.Has(3));
        Assert.False(_store.Has(1000)); // за пределами разреженного массива
        Assert.Equal(50, _store.Get(5));
        Assert.Equal(20, _store.Get(2));
        Assert.Throws<KeyNotFoundException>(() => _store.Get(3));
    }

    [Fact]
    public void Set_Existing_Replaces_WithoutSecondEntry()
    {
        _store.Set(1, 10);
        _store.Set(1, 11);

        Assert.Equal(1, _store.Count);
        Assert.Equal(11, _store.Get(1));
    }

    [Fact]
    public void Get_ReturnsReference_ChangeIsKept()
    {
        _store.Set(3, 1);
        _store.Get(3) += 41;

        Assert.Equal(42, _store.Get(3));
    }

    /// <summary>Удаление из середины переносит последнее значение на его место — и сущность, и значение должны совпасть.</summary>
    [Fact]
    public void Remove_FromMiddle_KeepsOthersIntact()
    {
        for (var i = 0; i < 5; i++)
            _store.Set(i, i * 10);

        Assert.True(_store.Remove(1));
        Assert.False(_store.Remove(1));

        Assert.False(_store.Has(1));
        Assert.Equal(new Dictionary<int, int> { [0] = 0, [2] = 20, [3] = 30, [4] = 40 }, Contents());
    }

    [Fact]
    public void AddAndRemove_InAnyOrder_MatchesReference()
    {
        var random = new Random(7);
        var reference = new Dictionary<int, int>();
        for (var step = 0; step < 5000; step++)
        {
            var entity = random.Next(200);
            if (random.Next(3) == 0)
            {
                Assert.Equal(reference.Remove(entity), _store.Remove(entity));
            }
            else
            {
                _store.Set(entity, step);
                reference[entity] = step;
            }
        }

        Assert.Equal(reference.Count, _store.Count);
        Assert.Equal(reference, Contents());
    }
}
