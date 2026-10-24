using System.Numerics;
using Api;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

/// <summary>Сквозное прохождение настоящих серверных действий без окна и подмены добычи инвентарём.</summary>
public sealed class StageSevenJourneyTests
{
    private sealed class Observer : IMod
    {
        public IPlayer Player = null!;
        public List<ItemPickedUp> Pickups { get; } = [];
        public List<PlayerDied> Deaths { get; } = [];
        public void Load(IModContext context)
        {
            context.Events.OnPlayerJoined(e => Player = e.Player);
            context.Events.OnItemPickedUp(Pickups.Add);
            context.Events.OnPlayerDied(Deaths.Add);
        }
    }

    [Fact]
    public void BareHandsToCopperPickaxe_ThenFatalFallRespawnAndRecoverEveryItem()
    {
        var observer = new Observer();
        var content = GameContent.Load();
        using var server = TestServers.Create(mods: [("journey", observer)]);
        var client = TestServers.JoinAndLoad(server);
        var inventory = observer.Player.Inventory;
        Assert.All(inventory.CopySlots(), stack => Assert.True(stack.IsEmpty));

        var spawn = BlockPos.Containing(server.World.SpawnPosition);
        int x = spawn.X + 8, z = spawn.Z, floor = spawn.Y + 4;
        var stone = content.Blocks.Get("base:stone").Id;
        // Контролируем только окружение: площадка, ресурсы и свободный воздух. Предметы получаются действиями игрока.
        for (int dx = -6; dx <= 6; dx++)
        for (int dz = -6; dz <= 6; dz++)
        for (int y = floor - 1; y <= floor + 30; y++)
            Assert.True(server.World.TrySetBlock(new BlockPos(x + dx, y, z + dz), y <= floor ? stone : BlockId.Air));
        var resource = new BlockPos(x, floor + 1, z);
        var miningFeet = new Vector3d(x + 0.5, floor + 3, z + 0.5);
        var workshopFeet = new Vector3d(x + 0.5, floor + 1, z + 0.5);

        ItemId Item(string name) => content.Items.Get(name).Id;
        int Count(string name) => inventory.Count(Item(name));
        void Hold(string? name)
        {
            int slot = Enumerable.Range(0, Inventory.Size).First(s => name == null ? inventory[s].IsEmpty : inventory[s].Item == Item(name));
            if (slot >= Inventory.HotbarSize)
            {
                client.Send(new InventoryClick(slot, InventoryClickKind.Primary));
                client.Send(new InventoryClick(8, InventoryClickKind.Primary));
                client.Send(new ReturnInventoryCursor());
                slot = 8;
            }
            client.Send(new SelectHotbarSlot(slot));
            server.Tick();
        }
        void Mine(string block, string? tool = null)
        {
            Hold(tool);
            int before = Count(block);
            Assert.True(server.World.TrySetBlock(resource, content.Blocks.Get(block).Id));
            TestServers.Teleport(server, client, miningFeet);
            client.Send(new UseItem(ItemAction.Primary, miningFeet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), -Vector3.UnitY));
            int ticks = Mining.BreakTicks(content.Blocks.Get(block).Mining, tool == null ? null : content.Items.Get(tool).Tool);
            TestServers.Tick(server, ticks + 1);
            Assert.True(server.World.Chunks.GetBlock(resource).IsAir);
            Assert.Equal(before, Count(block)); // добыча ещё на земле: прямое зачисление не проходит сценарий
            Assert.Contains(server.GroundItems, item => item.Stack.Item == Item(block));
            TestServers.TickUntil(server, () => Count(block) == before + 1, $"подбор {block}");
        }
        void Craft(string recipe, int times = 1)
        {
            TestServers.Teleport(server, client, workshopFeet);
            var result = content.Recipes.Get(recipe).Result;
            int before = inventory.Count(result.Item);
            for (int i = 0; i < times; i++) { client.Send(new CraftRecipe(recipe, false)); server.Tick(); }
            Assert.Equal(before + result.Count * times, inventory.Count(result.Item));
        }
        void PlaceStation(string name, int dx)
        {
            Hold(name);
            var feet = new Vector3d(x + dx + 0.5, floor + 3, z + 0.5);
            TestServers.Teleport(server, client, feet);
            client.Send(new UseItem(ItemAction.Secondary, feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), -Vector3.UnitY));
            server.Tick();
            Assert.Equal(content.Blocks.Get(name).Id, server.World.Chunks.GetBlock(new BlockPos(x + dx, floor + 1, z)));
            Assert.Equal(0, Count(name));
        }

        for (int i = 0; i < 3; i++) Mine("base:log");
        Craft("base:planks", 3);
        Craft("base:stick", 2);
        Craft("base:crafting_table");
        PlaceStation("base:crafting_table", -2);
        Craft("base:wooden_pickaxe");
        for (int i = 0; i < 11; i++) Mine("base:stone", "base:wooden_pickaxe");
        Craft("base:stone_pickaxe");
        Craft("base:furnace");
        PlaceStation("base:furnace", 2);
        for (int i = 0; i < 3; i++) Mine("base:copper_ore", "base:stone_pickaxe");
        Craft("base:copper_ingot", 3);
        Craft("base:copper_pickaxe");
        Assert.Equal(1, Count("base:copper_pickaxe"));
        Assert.Equal(17, observer.Pickups.Sum(pickup => pickup.Stack.Count)); // 3 бревна + 11 камня + 3 руды

        var belongings = inventory.CopySlots().Where(stack => !stack.IsEmpty).GroupBy(stack => stack.Item)
            .ToDictionary(group => group.Key, group => group.Sum(stack => stack.Count));
        var fallStart = workshopFeet + new Vector3d(0, 25, 0);
        TestServers.Teleport(server, client, fallStart);
        int teleportId = 0;
        while (client.TryReceive(out var message)) if (message is PlayerTeleport teleport) teleportId = teleport.TeleportId;
        var state = new PlayerState(fallStart);
        for (int tick = 0; tick < 100 && !state.OnGround; tick++)
        {
            state = PlayerPhysics.Step(state, default, server.World.Chunks, content.Blocks);
            client.Send(new PlayerMove(state.Position, default, teleportId));
            server.Tick();
        }
        Assert.True(state.OnGround);
        Assert.Equal(0, observer.Player.Health);
        Assert.Single(observer.Deaths);
        Assert.All(inventory.CopySlots(), stack => Assert.True(stack.IsEmpty));
        Assert.Equal(belongings.OrderBy(pair => pair.Key.Value), server.GroundItems.GroupBy(item => item.Stack.Item)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Stack.Count)).OrderBy(pair => pair.Key.Value));
        TestServers.Tick(server, 45); // на экране смерти таймер подбора истёк, но вещи остаются
        Assert.All(inventory.CopySlots(), stack => Assert.True(stack.IsEmpty));
        client.Send(new RespawnRequest());
        server.Tick();
        Assert.Equal(server.World.SpawnPosition, observer.Player.Position);
        Assert.Equal(20, observer.Player.Health);
        TestServers.Teleport(server, client, workshopFeet); // возвращаемся к месту падения
        TestServers.TickUntil(server, () => !server.GroundItems.Any(), "возвращение всех вещей после смерти");
        Assert.Equal(belongings.OrderBy(pair => pair.Key.Value), inventory.CopySlots().Where(stack => !stack.IsEmpty)
            .GroupBy(stack => stack.Item).ToDictionary(group => group.Key, group => group.Sum(stack => stack.Count)).OrderBy(pair => pair.Key.Value));
        Assert.Equal(1, Count("base:copper_pickaxe"));
        Assert.True(server.PlayerQuests(client.PlayerId).IsCompleted("base:copper_pickaxe"));
    }
}
