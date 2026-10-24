using Server.Storage;
using Shared;
using Shared.Items;
using Shared.Protocol;
using Shared.Content;
using Xunit;

namespace Server.Tests;

public sealed class GroundItemTests
{
    [Fact]
    public void DroppingFollowsPositiveYawTowardsPositiveX()
    {
        using var server = TestServers.Create();
        var client = TestServers.JoinAndLoad(server);
        var item = GameContent.Load().Items.All.First(i => !i.Id.IsAir && i.MaxStackSize > 5).Id;
        var position = server.PlayerPosition(client.PlayerId);
        server.PlayerInventory(client.PlayerId).Set(0, new ItemStack(item, 1));
        // Тот же yaw, что у Camera.Forward: поворот вправо на 90° смотрит вдоль +X.
        client.Send(new PlayerMove(position, new LookAngles(MathF.PI / 2, 0), 0));
        client.Send(new DropItem());
        server.Tick();
        var dropped = Assert.Single(server.GroundItems);
        Assert.True(dropped.Velocity.X > 0, $"Предмет должен лететь по +X, скорость: {dropped.Velocity}");
    }

    [Fact]
    public void DroppingOneConservesItemsAndWaitsBeforePickup()
    {
        using var server = TestServers.Create();
        var client = TestServers.JoinAndLoad(server);
        var item = GameContent.Load().Items.All.First(i => !i.Id.IsAir && i.MaxStackSize > 5).Id;
        var inventory = server.PlayerInventory(client.PlayerId);
        inventory.Set(0, new ItemStack(item, 5));
        client.Send(new DropItem(false));
        server.Tick();
        Assert.Equal(4, inventory[0].Count);
        Assert.Equal(1, server.GroundItems.Sum(i => i.Stack.Count));
        TestServers.Tick(server, 10);
        Assert.Equal(4, inventory[0].Count);
    }

    [Fact]
    public void PartialPickupLeavesRemainderAndPreservesCount()
    {
        using var server = TestServers.Create();
        var client = TestServers.JoinAndLoad(server);
        var item = GameContent.Load().Items.All.First(i => !i.Id.IsAir && i.MaxStackSize > 1);
        var inventory = server.PlayerInventory(client.PlayerId);
        for (var s = 0; s < Inventory.Size; s++) inventory.Set(s, new ItemStack(item.Id, item.MaxStackSize));
        inventory.Remove(0, 2);
        server.SpawnItem(server.PlayerPosition(client.PlayerId), new ItemStack(item.Id, 5), pickupDelay: 0);
        server.Tick();
        Assert.Equal(item.MaxStackSize, inventory[0].Count);
        Assert.Equal(3, server.GroundItems.Sum(i => i.Stack.Count));
    }

    [Fact]
    public void ExpiredItemIsRemovedByServerTicks()
    {
        using var server = TestServers.Create();
        var client = TestServers.JoinAndLoad(server);
        var item = GameContent.Load().Items.All.First(i => !i.Id.IsAir && i.MaxStackSize > 5).Id;
        server.SpawnItem(server.PlayerPosition(client.PlayerId), new ItemStack(item, 1), pickupDelay: 100,
            ageTicks: 5 * 60 * GameTime.TicksPerSecond - 1);
        server.Tick();
        Assert.Empty(server.GroundItems);
    }
    [Fact]
    public void MergeRespectsStackLimitAndDoesNotShortenPickupDelay()
    {
        using var server = TestServers.Create();
        var client = TestServers.JoinAndLoad(server);
        var item = GameContent.Load().Items.All.First(i => i.MaxStackSize > 5);
        var p = server.PlayerPosition(client.PlayerId);
        server.SpawnItem(p, new ItemStack(item.Id, item.MaxStackSize - 1), pickupDelay: 100);
        server.SpawnItem(p, new ItemStack(item.Id, 5), pickupDelay: 200);
        server.Tick();
        var stacks = server.GroundItems.OrderBy(i => i.Stack.Count).ToArray();
        Assert.Equal(2, stacks.Length);
        Assert.Equal(4, stacks[0].Stack.Count);
        Assert.Equal(item.MaxStackSize, stacks[1].Stack.Count);
        Assert.Equal(199, stacks[1].PickupDelayTicks);
    }

    [Fact]
    public void ItemIsSentOnlyInsideClientChunkViewAndRemovedAfterTeleport()
    {
        using var server = TestServers.Create();
        var client = TestServers.JoinAndLoad(server);
        var item = GameContent.Load().Items.All.First(i => i.MaxStackSize > 5).Id;
        var id = server.SpawnItem(server.PlayerPosition(client.PlayerId), new ItemStack(item, 1), pickupDelay: 1000);
        server.Tick();
        var messages = new List<ServerMessage>();
        while (client.TryReceive(out var m)) messages.Add(m);
        Assert.Contains(messages, m => m is EntitySpawned e && e.Id == id);
        TestServers.Teleport(server, client, server.PlayerPosition(client.PlayerId) + new Vector3d(1000, 0, 0));
        server.Tick();
        messages.Clear();
        while (client.TryReceive(out var m)) messages.Add(m);
        Assert.Contains(messages, m => m is EntityRemoved e && e.Id == id);
        Assert.DoesNotContain(messages, m => m is EntitySpawned e && e.Id == id);
    }

    [Fact]
    public void DroppedStackSurvivesServerRestart()
    {
        using var temp = new Server.Tests.Storage.TempDirectory();
        var item = GameContent.Load().Items.All.First(i => i.MaxStackSize > 5).Id;
        Vector3d position;
        using (var server = TestServers.Create(savePath: temp.SaveFile))
        {
            var client = TestServers.JoinAndLoad(server);
            position = server.PlayerPosition(client.PlayerId);
            server.SpawnItem(position, new ItemStack(item, 5), pickupDelay: 1000);
            server.Tick();
        }
        using (var server = TestServers.Create(savePath: temp.SaveFile))
        {
            TestServers.JoinAndLoad(server);
            Assert.Equal(5, server.GroundItems.Sum(i => i.Stack.Count));
            Assert.All(server.GroundItems, i => Assert.True(Vector3d.Distance(position, i.Position) < 1));
        }
    }

    [Fact]
    public void ThousandItemsCanTickWithoutLosingStacks()
    {
        using var server = TestServers.Create();
        var client = TestServers.JoinAndLoad(server);
        var item = GameContent.Load().Items.All.First(i => i.MaxStackSize > 5);
        var origin = server.PlayerPosition(client.PlayerId);
        for (var n = 0; n < 1000; n++)
            server.SpawnItem(origin + new Vector3d(n % 10, 5 + n / 100, n / 10 % 10),
                new ItemStack(item.Id, item.MaxStackSize), pickupDelay: 1000);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        TestServers.Tick(server, 20);
        timer.Stop();
        Assert.Equal(1000 * item.MaxStackSize, server.GroundItems.Sum(i => i.Stack.Count));
        var spawns = 0;
        var batches = 0;
        var moves = 0;
        while (client.TryReceive(out var message))
        {
            if (message is EntitySpawned) spawns++;
            if (message is EntitiesMoved moved) { batches++; moves += moved.Entities.Length; }
        }
        Console.WriteLine($"[Benchmark] 1000 предметов, 20 тиков: {timer.Elapsed.TotalMilliseconds:F2} мс; spawn={spawns}, move batches={batches}, positions={moves}");
    }
}
