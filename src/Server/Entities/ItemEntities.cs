using System.Numerics;
using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Physics;

namespace Server.Entities;

/// <summary>Предметы на земле: форма тела и создание.</summary>
internal static class ItemEntities
{
    /// <summary>
    /// Короб 0,25 блока; падает медленнее игрока и с меньшим пределом скорости, по земле быстро останавливается.
    /// Близко к Minecraft: брошенный предмет пролетает пару блоков и почти не скользит.
    /// </summary>
    public static readonly BodyShape Shape = new(Width: 0.25, Height: 0.25, Gravity: 16, TerminalVelocity: 40,
        AirDrag: 0.02f, GroundDrag: 0.4f);

    /// <summary>Скорость броска по Q, блоков в секунду: вперёд по взгляду и чуть вверх.</summary>
    public const float ThrowSpeed = 6f;
    public const float ThrowLift = 1.5f;

    /// <summary>Насколько ниже глаз вылетает брошенный предмет (верх короба — у груди).</summary>
    public const double ThrowDrop = 0.3;

    public static EntityId Spawn(EntityWorld entities, Vector3d position, Vector3 velocity, ItemStack stack)
    {
        var entity = entities.Create();
        entities.Set(entity, new Position { Value = position });
        entities.Set(entity, new Body { Velocity = velocity, Shape = Shape });
        entities.Set(entity, new DroppedItem { Stack = stack });
        entities.Set(entity, new Synced { SentPosition = position });
        return entity;
    }

    /// <summary>
    /// Точка и скорость брошенного игроком предмета. Он вылетает из центра тела игрока, а не перед ним: так он не
    /// окажется внутри стены, у которой игрок стоит, — стена просто остановит его полёт.
    /// </summary>
    public static (Vector3d Position, Vector3 Velocity) Throw(Vector3d feet, LookAngles look)
    {
        var y = feet.Y + PlayerPhysics.EyeHeight - ThrowDrop - Shape.Height;
        return (new Vector3d(feet.X, y, feet.Z), look.Forward * ThrowSpeed + new Vector3(0, ThrowLift, 0));
    }
}
