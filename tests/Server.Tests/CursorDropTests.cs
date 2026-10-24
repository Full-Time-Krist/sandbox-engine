using Server.Tests.Storage;
using Shared;
using Shared.Content;
using Shared.Items;
using Shared.Protocol;
using Xunit;

namespace Server.Tests;

public sealed class CursorDropTests
{
    [Fact]
    public void ClosingFullInventoryThrowsCursorWithoutLosingItems()
    {
        using var server = TestServers.Create();
        var client = TestServers.JoinAndLoad(server);
        var item = GameContent.Load().Items.Get("base:log");
        var inventory = server.PlayerInventory(client.PlayerId);
        for (int slot = 0; slot < Inventory.Size; slot++) inventory.Set(slot, new ItemStack(item.Id, item.MaxStackSize));
        inventory.Set(Inventory.CursorSlot, new ItemStack(item.Id, 3));
        client.Send(new ReturnInventoryCursor());
        server.Tick();
        Assert.True(inventory.Cursor.IsEmpty);
        Assert.Equal(3, server.GroundItems.Sum(i => i.Stack.Count));
    }

    [Fact]
    public void JoiningWithFullInventoryThrowsSavedCursor()
    {
        using var temp = new TempDirectory();
        var item = GameContent.Load().Items.Get("base:log");
        using (var server = TestServers.Create(savePath: temp.SaveFile))
        {
            var client = TestServers.JoinAndLoad(server);
            var inventory = server.PlayerInventory(client.PlayerId);
            for (int slot = 0; slot < Inventory.Size; slot++) inventory.Set(slot, new ItemStack(item.Id, item.MaxStackSize));
            inventory.Set(Inventory.CursorSlot, new ItemStack(item.Id, 3));
        }
        using (var server = TestServers.Create(savePath: temp.SaveFile))
        {
            var client = TestServers.JoinAndLoad(server);
            Assert.True(server.PlayerInventory(client.PlayerId).Cursor.IsEmpty);
            Assert.Equal(3, server.GroundItems.Sum(i => i.Stack.Count));
        }
    }
}
