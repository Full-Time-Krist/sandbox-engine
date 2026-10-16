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
/// Пределы зависят от режима (<see cref="MovementLimits"/>): в выживании — бег и прыжок, в творческом — быстрый полёт.
/// Гравитацию сервер не проверяет: нечестный клиент в выживании может висеть в воздухе и подниматься со скоростью прыжка.
/// </para>
/// </remarks>
internal sealed class MovementCheck(GameMode mode)
{
    /// <summary>На сколько тиков вперёд копится запас пути.</summary>
    public const int BudgetTicks = 10;

    public MovementLimits Limits { get; private set; } = MovementLimits.For(mode);

    private double _horizontal = MovementLimits.For(mode).Horizontal * BudgetTicks;
    private double _up = MovementLimits.For(mode).Up * BudgetTicks;
    private double _down = MovementLimits.For(mode).Down * BudgetTicks;

    /// <summary>Номер последнего телепорта этого игрока (см. <see cref="Shared.Protocol.PlayerTeleport"/>).</summary>
    public int TeleportId { get; private set; }

    /// <summary>Новый номер телепорта: сообщения клиента со старым номером дальше игнорируются.</summary>
    public int NextTeleport() => ++TeleportId;

    /// <summary>
    /// Меняет пределы под новый режим. Запас, накопленный по старым пределам, урезает первое же пополнение
    /// (<see cref="Refill"/> не даёт ему превысить новый предел). До него, в том же тике, перемещений по новым
    /// пределам не бывает: при переходе в выживание сервер телепортирует игрока, и всё, что клиент успел
    /// отправить, приходит со старым номером телепорта.
    /// </summary>
    public void SetMode(GameMode mode) => Limits = MovementLimits.For(mode);

    /// <summary>Пополняет запас пути. Вызывается раз в тик сервера.</summary>
    public void Refill()
    {
        _horizontal = Math.Min(_horizontal + Limits.Horizontal, Limits.Horizontal * BudgetTicks);
        _up = Math.Min(_up + Limits.Up, Limits.Up * BudgetTicks);
        _down = Math.Min(_down + Limits.Down, Limits.Down * BudgetTicks);
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

/// <summary>Наибольшее перемещение игрока за тик, в блоках: по горизонтали, вверх и вниз.</summary>
internal readonly record struct MovementLimits(double Horizontal, double Up, double Down)
{
    /// <summary>Запас на округление: скорость физики не превышает предела, но сравнение не должно быть впритык.</summary>
    private const double Slack = 1.05;

    private const double Tick = GameTime.TickDuration * Slack;

    /// <summary>
    /// Выживание: по горизонтали — бег, вверх — начало прыжка (дальше гравитация только замедляет), вниз — падение
    /// с предельной скоростью. Быстрее честный игрок в выживании не двигается: при переходе в него сервер
    /// телепортирует игрока на месте, и скорость полёта гаснет.
    /// </summary>
    public static readonly MovementLimits Survival =
        new(PlayerPhysics.SprintSpeed * Tick, PlayerPhysics.JumpVelocity * Tick, PlayerPhysics.TerminalVelocity * Tick);

    /// <summary>Творческий: по горизонтали и вверх — быстрый полёт, вниз — падение (оно быстрее полёта вниз).</summary>
    public static readonly MovementLimits Creative =
        new(PlayerPhysics.FlySprintSpeed * Tick, PlayerPhysics.FlySprintSpeed * Tick, PlayerPhysics.TerminalVelocity * Tick);

    public static MovementLimits For(GameMode mode) => mode == GameMode.Creative ? Creative : Survival;
}
