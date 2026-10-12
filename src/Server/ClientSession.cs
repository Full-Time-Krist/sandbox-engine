using System.Numerics;
using Shared.Networking;
using Shared.Protocol;

namespace Server;

/// <summary>Подключённый клиент с точки зрения сервера.</summary>
internal sealed class ClientSession(IConnection<ServerMessage, ClientMessage> connection)
{
    public IConnection<ServerMessage, ClientMessage> Connection { get; } = connection;

    public bool IsJoined { get; private set; }
    public int PlayerId { get; private set; }
    public string PlayerName { get; private set; } = "";
    public Vector3 PlayerPosition { get; set; }

    public void Join(int playerId, string playerName)
    {
        IsJoined = true;
        PlayerId = playerId;
        PlayerName = playerName;
    }
}
