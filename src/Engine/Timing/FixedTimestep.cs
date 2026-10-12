namespace Engine.Timing;

/// <summary>
/// Фиксированный шаг логики («Fix Your Timestep», Гленн Фидлер). Реальное время кадра копится в аккумуляторе,
/// из него отрезаются тики строго одинаковой длины. Поэтому логика детерминирована и не зависит от FPS.
/// </summary>
public sealed class FixedTimestep
{
    // Если кадр длился дольше (окно тащили мышью, сработал отладчик), лишнее время отбрасывается.
    // Иначе игра попыталась бы догнать его сотнями тиков подряд и зависла бы («спираль смерти»).
    public const double MaxFrameTime = 0.25;

    private double _accumulator;

    public int TicksPerSecond { get; }
    public double TickDuration { get; }

    /// <summary>
    /// Насколько далеко текущий момент ушёл от последнего тика, в долях тика [0; 1).
    /// Рендер использует это значение для интерполяции между предыдущим и текущим состоянием.
    /// </summary>
    public float Alpha => (float)(_accumulator / TickDuration);

    public FixedTimestep(int ticksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        TicksPerSecond = ticksPerSecond;
        TickDuration = 1.0 / ticksPerSecond;
    }

    /// <summary>Добавляет время кадра и возвращает, сколько тиков нужно выполнить.</summary>
    public int Advance(double frameTime)
    {
        _accumulator += Math.Min(frameTime, MaxFrameTime);

        var ticks = 0;
        while (_accumulator >= TickDuration)
        {
            _accumulator -= TickDuration;
            ticks++;
        }

        return ticks;
    }
}
