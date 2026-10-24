namespace Shared.Protocol;

/// <summary>Авторитетное здоровье и экран смерти; приходит при входе и каждом изменении.</summary>
public sealed record HealthChanged(int Health, int MaxHealth = 20, bool IsDead = false, string Reason = "") : ServerMessage;

/// <summary>Игрок нажал кнопку возрождения. Живой игрок этим сообщением не телепортируется.</summary>
public sealed record RespawnRequest : ClientMessage;
