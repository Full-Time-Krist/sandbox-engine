using System.Numerics;
using Shared.Blocks;
using Shared.Items;
using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

public sealed class ItemUseTests : IDisposable
{
    private static readonly Vector3 Down = -Vector3.UnitY;

    /// <summary>Верхний блок столбца (0, 0) и точка взгляда на 3,5 блока над ним.</summary>
    private readonly BlockPos _surface;
    private readonly Vector3 _eyeAbove;

    private readonly GameServer _server = TestServers.CreateLoaded();
    private readonly IConnection<ClientMessage, ServerMessage> _client;
    private readonly BlockRegistry _registry = new();
    private readonly BaseBlocks _blocks;

    public ItemUseTests()
    {
        _blocks = BaseBlocks.Register(_registry);
        _registry.Freeze();
        _client = Join(_server);
        _surface = TestServers.TopBlock(_server.World, 0, 0);
        _eyeAbove = new Vector3(0.5f, _surface.Y + 3.5f, 0.5f);
    }

    public void Dispose() => _server.Dispose();

    private static IConnection<ClientMessage, ServerMessage> Join(GameServer server)
    {
        var (client, serverSide) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
        server.Connect(serverSide);
        client.Send(new JoinRequest("Tester"));
        server.Tick();
        while (client.TryReceive(out _)) { }
        return client;
    }

    /// <summary>Отправляет сообщения от основного клиента, тикает сервер и возвращает полученные изменения блоков.</summary>
    private List<BlockChanged> Send(params ClientMessage[] messages)
    {
        foreach (var message in messages)
            _client.Send(message);
        _server.Tick();

        var changes = new List<BlockChanged>();
        while (_client.TryReceive(out var message))
            changes.Add(Assert.IsType<BlockChanged>(message));
        return changes;
    }

    private BlockId BlockAt(BlockPos pos) => _server.World.Chunks.GetBlock(pos);

    [Fact]
    public void Primary_BreaksTargetBlock()
    {
        var changes = Send(new PlayerMove(_eyeAbove), new UseItem(ItemAction.Primary, _eyeAbove, Down));

        Assert.Equal([new BlockChanged(_surface, BlockId.Air)], changes);
        Assert.True(BlockAt(_surface).IsAir);
    }

    [Fact]
    public void Secondary_PlacesSelectedBlock_OnFaceUnderCrosshair()
    {
        var above = _surface + new BlockPos(0, 1, 0);

        var changes = Send(new PlayerMove(_eyeAbove), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Equal([new BlockChanged(above, _blocks.Stone)], changes); // слот 1 — камень
        Assert.Equal(_blocks.Stone, BlockAt(above));
    }

    [Fact]
    public void SelectedSlot_DecidesPlacedBlock()
    {
        var above = _surface + new BlockPos(0, 1, 0);

        Send(new PlayerMove(_eyeAbove), new SelectHotbarSlot(5), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Equal(_blocks.Planks, BlockAt(above));
    }

    [Fact]
    public void InvalidSlot_IsIgnored()
    {
        var above = _surface + new BlockPos(0, 1, 0);

        Send(new PlayerMove(_eyeAbove), new SelectHotbarSlot(Hotbar.Size), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Equal(_blocks.Stone, BlockAt(above));
    }

    [Fact]
    public void EyeFarFromPlayer_IsRejected()
    {
        var farAway = _eyeAbove + new Vector3(0, 0, 20);
        var before = BlockAt(_surface);

        var changes = Send(new PlayerMove(farAway), new UseItem(ItemAction.Primary, _eyeAbove, Down));

        Assert.Empty(changes);
        Assert.Equal(before, BlockAt(_surface));
    }

    [Fact]
    public void CannotPlaceBlockIntoEye()
    {
        var eyeRightAbove = new Vector3(0.5f, _surface.Y + 1.5f, 0.5f);

        var changes = Send(new PlayerMove(eyeRightAbove), new UseItem(ItemAction.Secondary, eyeRightAbove, Down));

        Assert.Empty(changes);
    }

    [Fact]
    public void NothingInReach_DoesNothing()
    {
        var changes = Send(new PlayerMove(_eyeAbove), new UseItem(ItemAction.Primary, _eyeAbove, Vector3.UnitY));

        Assert.Empty(changes);
    }

    [Fact]
    public void InvalidNumbers_AreIgnored()
    {
        var nan = new Vector3(float.NaN);

        var changes = Send(new PlayerMove(_eyeAbove),
            new UseItem(ItemAction.Primary, nan, Down),
            new UseItem(ItemAction.Primary, _eyeAbove, nan),
            new UseItem(ItemAction.Primary, _eyeAbove, Vector3.Zero));

        Assert.Empty(changes);
    }

    [Fact]
    public void BlockChange_IsSentToEveryPlayer()
    {
        var second = Join(_server);

        Send(new PlayerMove(_eyeAbove), new UseItem(ItemAction.Primary, _eyeAbove, Down));

        Assert.True(second.TryReceive(out var message));
        Assert.Equal(new BlockChanged(_surface, BlockId.Air), message);
    }
}
