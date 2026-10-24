using System.Collections.Immutable;
using Shared.Entities;
using Shared.Items;

namespace Shared.Protocol;

/// <summary>Предмет появился в видимом чанке. Позиция — центр нижней грани физического тела.</summary>
public sealed record EntitySpawned(EntityId Id, Vector3d Position, ItemStack Stack) : ServerMessage;

/// <summary>Изменившиеся позиции сущностей за тик.</summary>
public sealed record EntitiesMoved(ImmutableArray<EntityMovement> Entities) : ServerMessage;
public readonly record struct EntityMovement(EntityId Id, Vector3d Position);

/// <summary>Сущность исчезла либо вышла из области видимости.</summary>
public sealed record EntityRemoved(EntityId Id) : ServerMessage;

/// <summary>Выбросить одну штуку из руки, либо всю стопку (Ctrl+Q).</summary>
public sealed record DropItem(bool All = false) : ClientMessage;
