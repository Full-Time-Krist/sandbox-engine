using System.Collections.Immutable;
using Shared.Blocks;
using Shared.Networking;
using Shared.Protocol;

namespace Server;

/// <summary>
/// Владелец мира: вся игровая логика выполняется здесь, клиенты только присылают действия и получают результат.
/// </summary>
/// <remarks>
/// Сервер не знает, кто и в каком потоке вызывает <see cref="Tick"/>. Сейчас это делает клиент в одиночной игре,
/// позже это может быть отдельный поток или выделенный сервер.
/// </remarks>
public sealed class GameServer
{
    private readonly List<ClientSession> _sessions = [];
    private readonly ImmutableArray<string> _blockNames;
    private int _nextPlayerId = 1;

    public World World { get; }

    public GameServer()
    {
        var registry = new BlockRegistry();
        var blocks = BaseBlocks.Register(registry);
        registry.Freeze();

        _blockNames = [.. registry.All.Select(b => b.Name)];
        World = new World(blocks);
    }

    public void Connect(IConnection<ServerMessage, ClientMessage> connection) => _sessions.Add(new ClientSession(connection));

    public void Tick()
    {
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
                session.Join(_nextPlayerId++, join.PlayerName);
                session.Connection.Send(new JoinAccepted(session.PlayerId, World.SpawnPosition, _blockNames));
                SendWorld(session);
                Console.WriteLine($"[Server] {session.PlayerName} подключился (id {session.PlayerId})");
                break;

            case PlayerMove move when session.IsJoined:
                session.PlayerPosition = move.Position;
                break;

            default:
                // До JoinRequest принимается только он сам. Незнакомое сообщение — ошибка протокола, а не повод падать.
                Console.WriteLine($"[Server] Сообщение {message.GetType().Name} отклонено (joined: {session.IsJoined})");
                break;
        }
    }

    /// <summary>Пока мир маленький, при подключении он отправляется целиком. На этапе 2 — только чанки вокруг игрока.</summary>
    private void SendWorld(ClientSession session)
    {
        foreach (var chunk in World.Chunks.All)
            session.Connection.Send(ChunkData.From(chunk));
    }
}
