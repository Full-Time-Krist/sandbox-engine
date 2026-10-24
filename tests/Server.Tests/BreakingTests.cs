using System.Numerics;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

/// <summary>Ломание удержанием на сервере: время по тикам, добыча по уровню инструмента, отмена.</summary>
public sealed class BreakingTests : IDisposable
{
    private static readonly Vector3 Down = new(0, -1, 0);

    private readonly GameServer _server = TestServers.Create();
    private readonly ContentRegistries _content = GameContent.Load();
    private readonly TestServers.TestClient _client;
    private readonly Inventory _inventory;
    private readonly BlockPos _surface;
    private readonly Vector3d _feet;

    public BreakingTests()
    {
        _client = TestServers.JoinAndLoad(_server);
        _surface = TestServers.TopBlock(_server.World, 0, 0);
        _feet = new Vector3d(0.5, _surface.Y + 2, 0.5);
        TestServers.Teleport(_server, _client, _feet);
        _inventory = _server.PlayerInventory(_client.PlayerId);
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
            _inventory.Set(slot, ItemStack.Empty);
        _server.Tick();
    }

    public void Dispose() => _server.Dispose();

    private BlockId Block(string name) => _content.Blocks.Get(name).Id;
    private ItemDefinition Item(string name) => _content.Items.Get(name);
    private BlockId Surface => _server.World.Chunks.GetBlock(_surface);

    /// <summary>Ставит блок на место верхнего блока, берёт предмет в руку (или пустую руку) и нажимает ЛКМ по блоку.</summary>
    private int StartBreaking(string block, string? held = null)
    {
        _server.World.TrySetBlock(_surface, Block(block));
        if (held != null)
            _inventory.Set(0, new ItemStack(Item(held).Id, 1));
        _client.Send(new UseItem(ItemAction.Primary, _feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), Down));
        return Mining.BreakTicks(_content.Blocks.Get(block).Mining, held == null ? null : Item(held).Tool);
    }

    private int CountOf(string item) =>
        Enumerable.Range(0, Inventory.Size).Where(s => _inventory[s].Item == Item(item).Id).Sum(s => _inventory[s].Count);

    [Fact]
    public void Grass_BreaksExactlyAfterItsTicks_NotEarlier()
    {
        var ticks = StartBreaking("base:grass");
        Assert.Equal(18, ticks);

        // Первый тик принимает нажатие; блок ломается, когда с него прошло ровно ticks тиков.
        TestServers.Tick(_server, ticks);
        Assert.Equal(Block("base:grass"), Surface);
        _server.Tick();
        Assert.True(Surface.IsAir);
        Assert.Equal(1, Assert.Single(_server.GroundItems).Stack.Count);
        TestServers.Tick(_server, 30); // задержка и притяжение: добыча сначала появляется в мире
        Assert.Equal(1, CountOf("base:grass"));
    }

    [Fact]
    public void StoneByHand_BreaksSlowlyWithoutDrop_WoodenPickaxeFastWithDrop()
    {
        var byHand = StartBreaking("base:stone");
        Assert.Equal(150, byHand); // 7,5 с
        TestServers.Tick(_server, byHand + 1);
        Assert.True(Surface.IsAir);
        Assert.Equal(0, CountOf("base:stone"));

        var withPickaxe = StartBreaking("base:stone", "base:wooden_pickaxe");
        Assert.Equal(23, withPickaxe); // 1,125 с
        TestServers.Tick(_server, withPickaxe + 1);
        Assert.True(Surface.IsAir);
        Assert.Equal(1, Assert.Single(_server.GroundItems).Stack.Count);
        TestServers.Tick(_server, 30); // задержка и притяжение: добыча сначала появляется в мире
        Assert.Equal(1, CountOf("base:stone"));
    }

    [Fact]
    public void CopperOre_NeedsStonePickaxe()
    {
        TestServers.Tick(_server, StartBreaking("base:copper_ore", "base:wooden_pickaxe") + 1);
        Assert.True(Surface.IsAir);
        Assert.Equal(0, CountOf("base:copper_ore"));

        TestServers.Tick(_server, StartBreaking("base:copper_ore", "base:stone_pickaxe") + 1);
        Assert.True(Surface.IsAir);
        Assert.Equal(1, Assert.Single(_server.GroundItems).Stack.Count);
        TestServers.Tick(_server, 30); // задержка и притяжение: добыча сначала появляется в мире
        Assert.Equal(1, CountOf("base:copper_ore"));
    }

    [Fact]
    public void StopBreaking_Cancels_NextPressStartsFromZero()
    {
        var ticks = StartBreaking("base:grass");
        TestServers.Tick(_server, ticks - 2);
        _client.Send(new StopBreaking());
        TestServers.Tick(_server, ticks);
        Assert.Equal(Block("base:grass"), Surface);

        // Новое нажатие начинает заново: почти всё время ещё нужно.
        _client.Send(new UseItem(ItemAction.Primary, _feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), Down));
        TestServers.Tick(_server, ticks - 2);
        Assert.Equal(Block("base:grass"), Surface);
        TestServers.Tick(_server, 3);
        Assert.True(Surface.IsAir);
    }

    [Fact]
    public void ChangingSlot_OrWalkingAway_Cancels()
    {
        var ticks = StartBreaking("base:grass");
        _client.Send(new SelectHotbarSlot(4));
        TestServers.Tick(_server, ticks + 5);
        Assert.Equal(Block("base:grass"), Surface);

        ticks = StartBreaking("base:grass");
        _server.Tick();
        TestServers.Teleport(_server, _client, _feet + new Vector3d(GameRules.BlockReach + 3, 0, 0));
        TestServers.Tick(_server, ticks + 5);
        Assert.Equal(Block("base:grass"), Surface);
    }

    [Fact]
    public void BlockChangedByAnotherAction_Cancels()
    {
        var ticks = StartBreaking("base:stone", "base:wooden_pickaxe");
        _server.Tick();
        _server.World.TrySetBlock(_surface, Block("base:dirt")); // другой блок на том же месте ломать не начинали
        TestServers.Tick(_server, ticks + 5);
        Assert.Equal(Block("base:dirt"), Surface);
    }

    [Fact]
    public void Creative_BreaksInOneTick()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        StartBreaking("base:copper_ore");
        _server.Tick();
        Assert.True(Surface.IsAir);
    }
}
