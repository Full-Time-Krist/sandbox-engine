namespace Shared.Entities;

/// <summary>Номер слота и поколение: повторное использование слота не оживляет старые ссылки.</summary>
public readonly record struct EntityId(int Index, uint Generation);
