using Shared;
using Shared.Blocks;
using Shared.Physics;
using Shared.Voxels;

namespace Server;

/// <summary>
/// Проверка движения одного игрока. Движение считает клиент; сервер не повторяет физику (он не знает нажатых
/// клавиш), а проверяет правдоподобие присланного перемещения:
/// <list type="bullet">
/// <item>скорость: перемещение не больше, чем успел бы пройти самый быстрый игрок (запас пути, см. ниже);</item>
/// <item>столкновения: тело, протянутое от старой позиции к новой (<see cref="PlayerPhysics.Sweep"/>), ни во что
/// не упирается. Честный клиент получил это перемещение тем же кодом, поэтому у него обрезки нет; а так нельзя
/// ни войти в стену, ни проскочить её насквозь.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Скорость считается не по одному сообщению, а по «запасу пути»: каждый тик сервера запас пополняется на
/// максимальное перемещение за тик, каждое принятое перемещение его тратит. Тики клиента и сервера идут
/// в разных потоках, и за один тик сервера может прийти два-три сообщения; запас это сглаживает, а копится
/// он не дольше <see cref="BudgetTicks"/> тиков, чтобы после стояния на месте нельзя было прыгнуть далеко.
/// </para>
/// <para>
/// Пределы пока одни для всех: самый быстрый способ двигаться — быстрый полёт. С режимами (шаг 3.5)
/// пределы будут зависеть от режима игрока.
/// </para>
/// </remarks>
internal sealed class MovementCheck
{
    /// <summary>На сколько тиков вперёд копится запас пути.</summary>
    public const int BudgetTicks = 10;

    /// <summary>Запас на округление: скорость физики не превышает предела, но сравнение не должно быть впритык.</summary>
    private const double Slack = 1.05;

    // Наибольшее перемещение за тик: по горизонтали и вверх — быстрый полёт, вниз — падение с предельной скоростью.
    internal const double MaxHorizontalPerTick = PlayerPhysics.FlySprintSpeed * GameTime.TickDuration * Slack;
    internal const double MaxUpPerTick = PlayerPhysics.FlySprintSpeed * GameTime.TickDuration * Slack;
    internal const double MaxDownPerTick = PlayerPhysics.TerminalVelocity * GameTime.TickDuration * Slack;

    private double _horizontal = MaxHorizontalPerTick * BudgetTicks;
    private double _up = MaxUpPerTick * BudgetTicks;
    private double _down = MaxDownPerTick * BudgetTicks;

    /// <summary>Номер последнего телепорта этого игрока (см. <see cref="Shared.Protocol.PlayerTeleport"/>).</summary>
    public int TeleportId { get; private set; }

    /// <summary>Новый номер телепорта: сообщения клиента со старым номером дальше игнорируются.</summary>
    public int NextTeleport() => ++TeleportId;

    /// <summary>Пополняет запас пути. Вызывается раз в тик сервера.</summary>
    public void Refill()
    {
        _horizontal = Math.Min(_horizontal + MaxHorizontalPerTick, MaxHorizontalPerTick * BudgetTicks);
        _up = Math.Min(_up + MaxUpPerTick, MaxUpPerTick * BudgetTicks);
        _down = Math.Min(_down + MaxDownPerTick, MaxDownPerTick * BudgetTicks);
    }

    /// <summary>
    /// Проверяет перемещение из <paramref name="from"/> в <paramref name="to"/> (точки ног). Возвращает null,
    /// если оно правдоподобно (и тратит запас пути), иначе — причину для лога.
    /// </summary>
    public string? Check(Vector3d from, Vector3d to, ChunkMap chunks, BlockRegistry blocks)
    {
        var delta = to - from;
        var horizontal = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        var up = Math.Max(delta.Y, 0);
        var down = Math.Max(-delta.Y, 0);

        if (horizontal > _horizontal || up > _up || down > _down)
            return $"слишком быстро: {horizontal:F2} по горизонтали, {delta.Y:F2} по высоте";

        // Незагруженный путь проверить нечем (у клиента таких чанков тоже нет, туда он и не пройдёт).
        if (PlayerPhysics.Sweep(from, delta, chunks, blocks) is { } moved
            && (Math.Abs(moved.X - delta.X) > Aabb.Epsilon || Math.Abs(moved.Y - delta.Y) > Aabb.Epsilon
                || Math.Abs(moved.Z - delta.Z) > Aabb.Epsilon))
        {
            return "сквозь блоки";
        }

        _horizontal -= horizontal;
        _up -= up;
        _down -= down;
        return null;
    }
}
