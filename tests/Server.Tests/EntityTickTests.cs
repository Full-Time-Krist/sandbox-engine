namespace Server.Tests;

/// <summary>Сущности в тике сервера: удалённые убираются в конце тика, а не сразу.</summary>
public class EntityTickTests
{
    [Fact]
    public void DestroyedEntity_IndexFreedAtEndOfTick()
    {
        using var server = TestServers.Create();
        var entities = server.Entities;
        var item = entities.Create();
        entities.Destroy(item);

        // До конца тика номер не переиспользуется: новая сущность получает другой индекс.
        var sameTick = entities.Create();
        Assert.NotEqual(item.Index, sameTick.Index);

        server.Tick();
        var nextTick = entities.Create();

        Assert.Equal(item.Index, nextTick.Index);
        Assert.False(entities.IsAlive(item));
        Assert.True(entities.IsAlive(nextTick));
    }
}
