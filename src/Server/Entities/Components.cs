using System.Numerics;
using Shared;
using Shared.Items;
using Shared.Physics;

namespace Server.Entities;

// Компоненты сущностей сервера. Обычные структуры с полями: системы меняют их на месте через EntityWorld.Get.

/// <summary>Точка тела сущности: центр нижней грани её короба. Абсолютная позиция — double.</summary>
internal struct Position
{
    public Vector3d Value;
}

/// <summary>Падающее тело: скорость, опора и форма (<see cref="BoxPhysics.Fall"/>). Двигает <see cref="MovementSystem"/>.</summary>
internal struct Body
{
    public Vector3 Velocity;
    public bool OnGround;
    public BodyShape Shape;
}

/// <summary>Предмет на земле: какая стопка в нём лежит, когда его можно подобрать и сколько он уже лежит.</summary>
internal struct DroppedItem
{
    public ItemStack Stack;

    /// <summary>Сколько тиков ещё нельзя подобрать: выброшенный иначе сразу вернулся бы в руку.</summary>
    public int PickupDelay;

    /// <summary>Сколько тиков предмет существует; через <see cref="ItemEntities.LifetimeTicks"/> исчезает.</summary>
    public int Age;
}

/// <summary>Позиция, разосланная клиентам в прошлом тике: по ней <see cref="EntitySync"/> видит, сдвинулась ли сущность.</summary>
internal struct Synced
{
    public Vector3d SentPosition;

    /// <summary>Разосланный поворот (у сущностей с <see cref="Facing"/>): повернулся — тоже сдвинулся для рассылки.</summary>
    public float SentYaw;

    /// <summary>Разосланный поворот головы относительно тела (<see cref="Facing.HeadTurn"/>).</summary>
    public float SentHeadTurn;
}
