using System.Numerics;
using Shared.Blocks;
using Shared.Items;
using Shared;
using Shared.Networking;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server.Tests;

public sealed class ItemUseTests : IDisposable
{
    private static readonly Vector3 Down = -Vector3.UnitY;

    /// <summary>
    /// Верхний блок столбца (0, 0) и игрок, висящий в полёте ногами на блок выше его верха: блок, поставленный
    /// на поверхность, касается тела, но не пересекает его. Глаз — на <see cref="PlayerPhysics.EyeHeight"/> выше ног.
    /// </summary>
    private readonly BlockPos _surface;
    private readonly Vector3d _feetAbove;
    private readonly Vector3d _eyeAbove;

    private readonly GameServer _server = TestServers.Create();
    private readonly IConnection<ClientMessage, ServerMessage> _client;
    private readonly BlockRegistry _registry = new();
    private readonly BaseBlocks _blocks;

    public ItemUseTests()
    {
        _blocks = BaseBlocks.Register(_registry);
        _registry.Freeze();
        _client = TestServers.JoinAndLoad(_server);
        _surface = TestServers.TopBlock(_server.World, 0, 0);
        _feetAbove = new Vector3d(0.5, _surface.Y + 2, 0.5);
        _eyeAbove = EyeOf(_feetAbove);
    }

    public void Dispose() => _server.Dispose();

    /// <summary>Отправляет сообщения от основного клиента, тикает сервер и возвращает полученные изменения блоков.</summary>
    private List<BlockChanged> Send(params ClientMessage[] messages)
    {
        foreach (var message in messages)
            _client.Send(message);
        _server.Tick();

        // Если игрок перешёл в другой чанк, вместе с изменениями могут прийти чанки: они здесь не важны.
        var changes = new List<BlockChanged>();
        while (_client.TryReceive(out var message))
        {
            if (message is BlockChanged change)
                changes.Add(change);
        }
        return changes;
    }

    private BlockId BlockAt(BlockPos pos) => _server.World.Chunks.GetBlock(pos);

    private static Vector3d EyeOf(Vector3d feet) => feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0);

    [Fact]
    public void Primary_BreaksTargetBlock()
    {
        var changes = Send(new PlayerMove(_feetAbove), new UseItem(ItemAction.Primary, _eyeAbove, Down));

        Assert.Equal([new BlockChanged(_surface, BlockId.Air)], changes);
        Assert.True(BlockAt(_surface).IsAir);
    }

    [Fact]
    public void Secondary_PlacesSelectedBlock_OnFaceUnderCrosshair()
    {
        var above = _surface + new BlockPos(0, 1, 0);

        var changes = Send(new PlayerMove(_feetAbove), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Equal([new BlockChanged(above, _blocks.Stone)], changes); // слот 1 — камень
        Assert.Equal(_blocks.Stone, BlockAt(above));
    }

    [Fact]
    public void SelectedSlot_DecidesPlacedBlock()
    {
        var above = _surface + new BlockPos(0, 1, 0);

        Send(new PlayerMove(_feetAbove), new SelectHotbarSlot(5), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Equal(_blocks.Planks, BlockAt(above));
    }

    [Fact]
    public void InvalidSlot_IsIgnored()
    {
        var above = _surface + new BlockPos(0, 1, 0);

        Send(new PlayerMove(_feetAbove), new SelectHotbarSlot(Hotbar.Size), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Equal(_blocks.Stone, BlockAt(above));
    }

    [Fact]
    public void EyeFarFromPlayer_IsRejected()
    {
        var farAway = _feetAbove + new Vector3(0, 0, 20);
        var before = BlockAt(_surface);

        var changes = Send(new PlayerMove(farAway), new UseItem(ItemAction.Primary, _eyeAbove, Down));

        Assert.Empty(changes);
        Assert.Equal(before, BlockAt(_surface));
    }

    [Fact]
    public void CannotPlaceBlockIntoOwnBody()
    {
        // Стоит на поверхности: блок над ней — у него в ногах.
        var standing = new Vector3d(0.5, _surface.Y + 1, 0.5);

        var changes = Send(new PlayerMove(standing), new UseItem(ItemAction.Secondary, EyeOf(standing), Down));

        Assert.Empty(changes);
    }

    [Fact]
    public void CannotPlaceBlockIntoOtherPlayer()
    {
        var other = TestServers.JoinAndLoad(_server);
        other.Send(new PlayerMove(new Vector3d(0.5, _surface.Y + 1, 0.5)));
        // Сессии обрабатываются по порядку подключения: без отдельного тика действие первого игрока
        // выполнилось бы раньше, чем сервер примет новую позицию второго.
        _server.Tick();

        var changes = Send(new PlayerMove(_feetAbove), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Empty(changes);
    }

    [Fact]
    public void CanPlaceBlockNextToOtherPlayer()
    {
        // Второй игрок стоит в соседнем столбце вплотную к месту установки: касание — не пересечение.
        var other = TestServers.JoinAndLoad(_server);
        var otherSurface = TestServers.TopBlock(_server.World, 1, 0);
        other.Send(new PlayerMove(new Vector3d(1 + PlayerPhysics.Width / 2, otherSurface.Y + 1, 0.5)));
        _server.Tick();

        var changes = Send(new PlayerMove(_feetAbove), new UseItem(ItemAction.Secondary, _eyeAbove, Down));

        Assert.Single(changes);
    }

    [Fact]
    public void NothingInReach_DoesNothing()
    {
        var changes = Send(new PlayerMove(_feetAbove), new UseItem(ItemAction.Primary, _eyeAbove, Vector3.UnitY));

        Assert.Empty(changes);
    }

    [Fact]
    public void InvalidNumbers_AreIgnored()
    {
        var nan = new Vector3(float.NaN);

        var changes = Send(new PlayerMove(_feetAbove),
            new UseItem(ItemAction.Primary, new Vector3d(double.NaN, 0, 0), Down),
            new UseItem(ItemAction.Primary, _eyeAbove, nan),
            new UseItem(ItemAction.Primary, _eyeAbove, Vector3.Zero));

        Assert.Empty(changes);
    }

    [Fact]
    public void BlockChange_IsSentToEveryPlayer()
    {
        var second = TestServers.JoinAndLoad(_server);

        Send(new PlayerMove(_feetAbove), new UseItem(ItemAction.Primary, _eyeAbove, Down));

        Assert.True(second.TryReceive(out var message));
        Assert.Equal(new BlockChanged(_surface, BlockId.Air), message);
    }
}
