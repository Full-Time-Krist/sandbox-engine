using System.Numerics;
using Server.Storage;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests.Storage;

/// <summary>Сохранение глазами сервера: перезапуск, выгрузка чанков, автосохранение.</summary>
public sealed class SaveLoadTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private GameServer Create(int? seed = TestServers.Seed) => TestServers.Create(savePath: _directory.SaveFile, seed: seed);

    /// <summary>Ломает верхний блок столбца (0, 0) так же, как игрок: через сообщения. Возвращает его позицию.</summary>
    private static BlockPos BreakSurface(GameServer server, TestServers.TestClient client)
    {
        var surface = TestServers.TopBlock(server.World, 0, 0);
        var eye = new Vector3d(0.5, surface.Y + 3.5, 0.5);
        TestServers.Teleport(server, client, eye);
        client.Send(new UseItem(ItemAction.Primary, eye, -Vector3.UnitY));
        TestServers.Tick(server, TestServers.HoldBreakTicks);

        Assert.True(server.World.Chunks.GetBlock(surface).IsAir);
        return surface;
    }

    [Fact]
    public void BrokenBlock_SurvivesRestart_AndSeedComesFromSave()
    {
        BlockPos surface;
        using (var server = Create(seed: 5))
            surface = BreakSurface(server, TestServers.JoinAndLoad(server));

        using var restarted = Create(seed: 99);
        TestServers.JoinAndLoad(restarted);

        Assert.Equal(5, restarted.World.Seed);
        Assert.True(restarted.World.Chunks.GetBlock(surface).IsAir);
    }

    [Fact]
    public void GeneratedChunks_AreSaved_EvenUnchanged()
    {
        // Сохраняется каждый сгенерированный чанк, а не только изменённые: генератор будет меняться,
        // и нетронутые места не должны пересоздаваться по-новому.
        using (var server = Create())
            TestServers.JoinAndLoad(server);

        using var restarted = Create();
        TestServers.JoinAndLoad(restarted);

        Assert.Equal(0, restarted.World.GeneratedCount);
        Assert.True(restarted.World.LoadedCount >= TestServers.AreaSize());
    }

    [Fact]
    public void Player_StartsWhereHeLeft()
    {
        var position = new Vector3d(40.25, 90.5, -7.75);
        using (var server = Create())
        {
            var client = TestServers.JoinAndLoad(server);
            TestServers.Teleport(server, client, position);
            server.Tick();
        }

        using var restarted = Create();
        var messages = new List<ServerMessage>();
        TestServers.JoinAndLoad(restarted, messages);

        Assert.Equal(position, Assert.IsType<JoinAccepted>(messages[0]).StartPosition);
    }

    [Fact]
    public void ChangedChunk_SurvivesUnload_AndComesBackChanged()
    {
        using var server = Create();
        var client = TestServers.JoinAndLoad(server);
        var surface = BreakSurface(server, client);
        var chunk = surface.Chunk;

        // Улетаем так далеко, что сервер выгружает чанк из памяти, и возвращаемся.
        var eye = new Vector3d(0.5, surface.Y + 3.5, 0.5);
        TestServers.Teleport(server, client, eye + new Vector3d(32 * 20, 0, 0));
        TestServers.TickUntil(server, () => !server.World.Chunks.Contains(chunk), "сервер выгрузит чанк");
        TestServers.Teleport(server, client, eye);
        TestServers.TickUntil(server, () => server.World.Chunks.Contains(chunk), "чанк загрузится снова");

        Assert.True(server.World.Chunks.GetBlock(surface).IsAir);
        Assert.True(server.World.LoadedCount > 0);
    }

    [Fact]
    public void Autosave_WritesChangedChunks_WithoutExit()
    {
        using var server = Create();
        var surface = BreakSurface(server, TestServers.JoinAndLoad(server));
        Assert.Equal(1, server.World.DirtyCount);

        for (var i = 0; i < GameServer.AutosaveIntervalTicks; i++)
            server.Tick();
        Assert.Equal(0, server.World.DirtyCount);

        // Игра ещё идёт, но изменение уже на диске: его видит второй читатель того же файла.
        server.Save!.Flush();
        var registry = GameContent.Load().Blocks;
        using var reader = WorldSave.Open(_directory.SaveFile, registry, WorldSaveTests.ItemsFor(registry));
        Assert.True(reader.TryLoadChunk(surface.Chunk, out var saved));
        Assert.True(saved.Get(surface.Local).IsAir);
    }

    /// <summary>Номер последнего телепорта, который получил клиент: с ним клиент шлёт PlayerMove.</summary>
    private static int LastTeleportId(TestServers.TestClient client)
    {
        var id = 0;
        while (client.TryReceive(out var message))
        {
            if (message is PlayerTeleport teleport)
                id = teleport.TeleportId;
        }
        return id;
    }

    [Fact]
    public void PlayerState_SurvivesRestart_CursorReturnsToInventory()
    {
        var look = new LookAngles(1.25f, -0.5f);
        ItemStack mined;
        using (var server = Create())
        {
            // Всё — сообщениями, как настоящий клиент: добыча, выбор слота, взгляд, клик по инвентарю, смена режима.
            var client = TestServers.JoinAndLoad(server);
            var surface = BreakSurface(server, client);
            var inventory = server.PlayerInventory(client.PlayerId);
            mined = inventory[0];
            Assert.False(mined.IsEmpty);
            inventory.Set(10, new ItemStack(mined.Item, 7));

            client.Send(new SelectHotbarSlot(4));
            client.Send(new PlayerMove(server.PlayerPosition(client.PlayerId), look, LastTeleportId(client)));
            client.Send(new InventoryClick(0, InventoryClickKind.Primary)); // стопка из слота 0 — на курсор
            client.Send(new SetGameMode(GameMode.Creative));
            server.Tick();
            Assert.Equal(mined, inventory.Cursor);
            Assert.True(server.World.Chunks.GetBlock(surface).IsAir);
        }

        using var restarted = Create();
        var messages = new List<ServerMessage>();
        TestServers.JoinAndLoad(restarted, messages);

        var accepted = Assert.IsType<JoinAccepted>(messages[0]);
        Assert.Equal(look, accepted.StartLook);
        Assert.Equal(GameMode.Creative, accepted.Mode);
        var contents = Assert.IsType<InventoryContents>(messages[1]);
        Assert.Equal(4, contents.SelectedSlot);
        // Предмет с курсора вернулся в слоты: доложен в стопку того же предмета в слоте 10.
        Assert.True(contents.Slots[Inventory.CursorSlot].IsEmpty);
        Assert.Equal(new ItemStack(mined.Item, 7 + mined.Count), contents.Slots[10]);
        Assert.Equal(1, contents.Slots.Count(s => !s.IsEmpty));
    }

    [Fact]
    public void NewPlayer_GetsWorldDefaultMode_ExistingWorldKeepsIts()
    {
        using (var server = TestServers.Create(savePath: _directory.SaveFile, defaultMode: GameMode.Creative))
        {
            var messages = new List<ServerMessage>();
            TestServers.JoinAndLoad(server, messages);
            Assert.Equal(GameMode.Creative, Assert.IsType<JoinAccepted>(messages[0]).Mode);
        }

        // Режим задаётся только при создании мира: у уже созданного он свой.
        using var restarted = TestServers.Create(savePath: _directory.SaveFile, defaultMode: GameMode.Survival);
        Assert.Equal(GameMode.Creative, restarted.World.DefaultMode);
    }
}
