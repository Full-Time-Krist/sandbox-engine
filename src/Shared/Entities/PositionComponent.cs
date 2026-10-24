namespace Shared.Entities;

/// <summary>Авторитетная позиция сущности; для игрока — точка ног.</summary>
public record struct PositionComponent(Vector3d Value);

/// <summary>Здоровье: ноль означает смерть до запроса возрождения.</summary>
public record struct HealthComponent(int Value);
