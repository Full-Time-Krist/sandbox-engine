using System.Numerics;

namespace Shared.Protocol;

/// <summary>Сообщение от сервера клиенту.</summary>
public abstract record ServerMessage;

public sealed record JoinAccepted(int PlayerId) : ServerMessage;

/// <summary>Новая позиция сущности мира после тика сервера.</summary>
public sealed record EntityMoved(int EntityId, Vector3 Position) : ServerMessage;
