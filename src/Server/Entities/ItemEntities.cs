using System.Numerics;
using Shared;
using Shared.Entities;
using Shared.Items;
using Shared.Physics;
using Shared.Voxels;

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

    /// <summary>Задержка подбора добычи сломанного блока: 0,5 с — чтобы выпавший предмет было видно.</summary>
    public const int DropPickupDelay = GameTime.TicksPerSecond / 2;

    /// <summary>Задержка подбора выброшенного: 2 с — иначе он сразу вернулся бы к бросившему.</summary>
    public const int ThrowPickupDelay = GameTime.TicksPerSecond * 2;

    /// <summary>Лежащий предмет исчезает через 5 минут: тысячи предметов от добычи не копятся бесконечно.</summary>
    public const int LifetimeTicks = GameTime.TicksPerSecond * 60 * 5;

    // Разлёт добычи из сломанного блока, блоков в секунду: немного в сторону и вверх.
    private const float DropSpread = 1f;
    private const float DropLift = 3f;

    public static EntityId Spawn(EntityWorld entities, Vector3d position, Vector3 velocity, ItemStack stack, int pickupDelay = 0)
    {
        var entity = entities.Create();
        entities.Set(entity, new Position { Value = position });
        entities.Set(entity, new Body { Velocity = velocity, Shape = Shape });
        entities.Set(entity, new DroppedItem { Stack = stack, PickupDelay = pickupDelay });
        entities.Set(entity, new Synced { SentPosition = position });
        return entity;
    }

    /// <summary>Добыча сломанного блока: из его центра, с небольшим случайным разлётом.</summary>
    public static EntityId SpawnDrop(EntityWorld entities, BlockPos block, ItemStack stack, Random random)
    {
        var position = new Vector3d(block.X + 0.5, block.Y + 0.5 - Shape.Height / 2, block.Z + 0.5);
        var velocity = new Vector3((random.NextSingle() * 2 - 1) * DropSpread, DropLift, (random.NextSingle() * 2 - 1) * DropSpread);
        return Spawn(entities, position, velocity, stack, DropPickupDelay);
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
