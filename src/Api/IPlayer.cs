using Shared;
using Shared.Items;

namespace Api;

/// <summary>Игрок на сервере, как его видят моды.</summary>
public interface IPlayer
{
    string Name { get; }

    /// <summary>Последняя принятая сервером позиция ног.</summary>
    Vector3d Position { get; }

    GameMode Mode { get; }

    /// <summary>Текущее здоровье (0–20); ноль — игрок ожидает возрождения.</summary>
    int Health { get; }

    /// <summary>Нанести положительное количество урона. Творческий режим и погибший игрок невосприимчивы.</summary>
    void Damage(int amount, string reason);

    /// <summary>Авторитетный инвентарь: изменения уходят клиенту в конце тика.</summary>
    Inventory Inventory { get; }
}
