using System.Numerics;
using Shared;
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
    // Временная сущность для проверки связи клиент ↔ сервер: куб, летающий по кругу.
    private const int OrbitingCubeId = 1;

    // Центр орбиты перед стартовой точкой камеры, чтобы куб был в кадре и не пролетал сквозь камеру.
    private static readonly Vector3 OrbitCenter = new(0.5f, 1.5f, -4f);
    private const float OrbitRadius = 3f;
    private const float OrbitSpeed = 0.5f; // рад/с

    private readonly List<ClientSession> _sessions = [];
    private int _nextPlayerId = 1;
    private long _tick;

    public void Connect(IConnection<ServerMessage, ClientMessage> connection) => _sessions.Add(new ClientSession(connection));

    public void Tick()
    {
        foreach (var session in _sessions)
        {
            while (session.Connection.TryReceive(out var message))
                Handle(session, message);
        }

        _tick++;

        var angle = _tick * GameTime.TickDuration * OrbitSpeed;
        var (sin, cos) = MathF.SinCos(angle);
        Broadcast(new EntityMoved(OrbitingCubeId, OrbitCenter + new Vector3(cos, 0f, sin) * OrbitRadius));
    }

    private void Handle(ClientSession session, ClientMessage message)
    {
        switch (message)
        {
            case JoinRequest join when !session.IsJoined:
                session.Join(_nextPlayerId++, join.PlayerName);
                session.Connection.Send(new JoinAccepted(session.PlayerId));
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

    private void Broadcast(ServerMessage message)
    {
        foreach (var session in _sessions)
        {
            if (session.IsJoined)
                session.Connection.Send(message);
        }
    }
}
