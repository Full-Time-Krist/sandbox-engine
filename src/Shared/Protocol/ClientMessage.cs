using System.Numerics;

namespace Shared.Protocol;

/// <summary>Сообщение от клиента серверу.</summary>
public abstract record ClientMessage;

/// <summary>Первое сообщение после подключения.</summary>
public sealed record JoinRequest(string PlayerName) : ClientMessage;

/// <summary>
/// Позиция игрока после тика клиента. Пока движение считает клиент (как в Minecraft),
/// позже сервер начнёт её проверять.
/// </summary>
public sealed record PlayerMove(Vector3 Position) : ClientMessage;
