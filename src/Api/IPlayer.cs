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

    /// <summary>Авторитетный инвентарь: изменения уходят клиенту в конце тика.</summary>
    Inventory Inventory { get; }

    /// <summary>Здоровье (0 — погиб и ещё не возродился).</summary>
    int Health { get; }

    int MaxHealth { get; }

    /// <summary>
    /// Ранит игрока на <paramref name="amount"/> единиц с причиной <paramref name="cause"/> (имя вида <c>my_mod:spikes</c>):
    /// событие <see cref="PlayerDamaged"/>, до нуля — смерть. В творческом и у погибшего ничего не делает.
    /// </summary>
    void Damage(int amount, string cause);
}
