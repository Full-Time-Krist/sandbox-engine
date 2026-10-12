using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using Server.Items;
using Shared;
using Shared.Blocks;
using Shared.Items;
using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Владелец мира: вся игровая логика выполняется здесь, клиенты только присылают действия и получают результат.
/// </summary>
/// <remarks>
/// Сервер не знает, кто и в каком потоке вызывает <see cref="Tick"/>: в одиночной игре это <see cref="ServerHost"/>,
/// в тестах — сам тест. Всё состояние сервера меняется только внутри <see cref="Tick"/>, из одного потока.
/// </remarks>
public sealed class GameServer
{
    /// <summary>
    /// Насколько присланная точка взгляда может отличаться от последней известной позиции игрока.
    /// Клиент рисует камеру с интерполяцией и шлёт позицию раз в тик, так что небольшое расхождение нормально;
    /// большое означает ошибку или попытку действовать издалека.
    /// </summary>
    private const float MaxEyeOffset = 4f;

    private readonly List<ClientSession> _sessions = [];

    /// <summary>Подключения, которые ещё не приняты в тике. Пополняются из любого потока.</summary>
    private readonly ConcurrentQueue<IConnection<ServerMessage, ClientMessage>> _pendingConnections = new();
    private readonly ImmutableArray<string> _blockNames;
    private readonly BaseBlocks _blocks;
    private readonly WorldEditor _editor;
    private int _nextPlayerId = 1;

    public World World { get; }

    public GameServer()
    {
        var registry = new BlockRegistry();
        _blocks = BaseBlocks.Register(registry);
        registry.Freeze();

        _blockNames = [.. registry.All.Select(b => b.Name)];
        World = new World(_blocks);
        _editor = new WorldEditor(this);
    }

    /// <summary>
    /// Подключает клиента. Можно вызывать из любого потока: сессия появится в начале следующего тика,
    /// потому что список сессий принадлежит потоку сервера.
    /// </summary>
    public void Connect(IConnection<ServerMessage, ClientMessage> connection) => _pendingConnections.Enqueue(connection);

    public void Tick()
    {
        while (_pendingConnections.TryDequeue(out var connection))
            _sessions.Add(new ClientSession(connection));

        foreach (var session in _sessions)
        {
            while (session.Connection.TryReceive(out var message))
                Handle(session, message);
        }
    }

    private void Handle(ClientSession session, ClientMessage message)
    {
        switch (message)
        {
            case JoinRequest join when !session.IsJoined:
                session.Join(_nextPlayerId++, join.PlayerName, Hotbar.CreateDefault(_blocks));
                // До первого PlayerMove игрок стоит в точке появления, иначе его действия сочлись бы «издалека».
                session.PlayerPosition = World.SpawnPosition;
                session.Connection.Send(new JoinAccepted(session.PlayerId, World.SpawnPosition, _blockNames));
                SendWorld(session);
                Console.WriteLine($"[Server] {session.PlayerName} подключился (id {session.PlayerId})");
                break;

            case PlayerMove move when session.IsJoined:
                session.PlayerPosition = move.Position;
                break;

            case SelectHotbarSlot select when session.IsJoined:
                if (!session.Hotbar.TrySelect(select.Slot))
                    Console.WriteLine($"[Server] {session.PlayerName}: неверный слот {select.Slot}");
                break;

            case UseItem use when session.IsJoined:
                HandleUseItem(session, use);
                break;

            default:
                // До JoinRequest принимается только он сам. Незнакомое сообщение — ошибка протокола, а не повод падать.
                Console.WriteLine($"[Server] Сообщение {message.GetType().Name} отклонено (joined: {session.IsJoined})");
                break;
        }
    }

    private void HandleUseItem(ClientSession session, UseItem use)
    {
        if (!float.IsFinite(use.EyePosition.LengthSquared()) || !float.IsFinite(use.LookDirection.LengthSquared()))
            return;

        if (Vector3.Distance(use.EyePosition, session.PlayerPosition) > MaxEyeOffset)
        {
            Console.WriteLine($"[Server] {session.PlayerName}: действие отклонено, точка взгляда далеко от игрока");
            return;
        }

        // Сервер сам повторяет луч по своей копии мира: клиент не может указать произвольный блок.
        var target = VoxelRaycast.Cast(World.Chunks, use.EyePosition, use.LookDirection, GameRules.BlockReach);
        var item = session.Hotbar.SelectedItem;
        ItemBehaviors.For(item).Use(use.Action, new ItemUseContext(_editor, item, target, use.EyePosition));
    }

    private void Broadcast(ServerMessage message)
    {
        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                session.Connection.Send(message);
        }
    }

    /// <summary>Единственная точка изменения блоков на сервере: меняет мир и сообщает всем клиентам.</summary>
    private sealed class WorldEditor(GameServer server) : IBlockEditor
    {
        public BlockId GetBlock(BlockPos pos) => server.World.Chunks.GetBlock(pos);

        public bool TrySetBlock(BlockPos pos, BlockId block)
        {
            if (!server.World.Chunks.TrySetBlock(pos, block))
                return false;

            server.Broadcast(new BlockChanged(pos, block));
            return true;
        }
    }

    /// <summary>Пока мир маленький, при подключении он отправляется целиком. На этапе 2 — только чанки вокруг игрока.</summary>
    private void SendWorld(ClientSession session)
    {
        var count = 0;
        long bytes = 0;
        foreach (var chunk in World.Chunks.All)
        {
            var data = ChunkData.From(chunk);
            session.Connection.Send(data);
            count++;
            bytes += data.Data.Length;
        }

        Console.WriteLine($"[Server] {session.PlayerName}: отправлено {count} чанков, {bytes / 1024.0:F0} КБ " +
                          $"(без сжатия {count * ChunkMath.Volume * 2 / 1024 / 1024.0:F1} МБ)");
    }
}
