using Server.Tests.Storage;
using Shared;
using Shared.Content;
using Shared.Items;
using Shared.Protocol;
using Shared.Voxels;
using Xunit;

namespace Server.Tests;

public sealed class ItemStreamingTests
{
    [Fact]
    public void UnseenItemAppearsAfterChunkAndSurvivesUnloadReload()
    {
        using var temp = new TempDirectory();
        using var server = TestServers.Create(savePath: temp.SaveFile);
        var client = TestServers.JoinAndLoad(server);
        var origin = server.PlayerPosition(client.PlayerId);
        var far = origin + new Vector3d(1000, 0, 0);
        var chunk = BlockPos.Containing(far).Chunk;
        server.World.EnsureChunk(chunk);
        var item = GameContent.Load().Items.Get("base:log").Id;
        server.SpawnItem(far, new ItemStack(item, 5), pickupDelay: 1000);
        server.Tick();
        Assert.DoesNotContain(Drain(client), m => m is EntitySpawned);
        TestServers.Teleport(server, client, far);
        var seen = new List<ServerMessage>();
        TestServers.TickUntil(server, () =>
        {
            seen.AddRange(Drain(client));
            return seen.Any(m => m is EntitySpawned);
        }, "предмет отправлен вместе со своим чанком");
        Assert.True(seen.FindIndex(m => m is ChunkData c && c.Position == chunk) < seen.FindIndex(m => m is EntitySpawned));
        TestServers.Teleport(server, client, origin);
        TestServers.TickUntil(server, () => !server.World.Chunks.Contains(chunk), "выгрузка чанка предмета");
        Assert.Empty(server.GroundItems);
        TestServers.Teleport(server, client, far);
        TestServers.TickUntil(server, () => server.GroundItems.Any(), "загрузка предмета с чанком");
        Assert.Equal(5, server.GroundItems.Sum(i => i.Stack.Count));
    }

    private static List<ServerMessage> Drain(TestServers.TestClient client)
    {
        var result = new List<ServerMessage>();
        while (client.TryReceive(out var message)) result.Add(message);
        return result;
    }
}
