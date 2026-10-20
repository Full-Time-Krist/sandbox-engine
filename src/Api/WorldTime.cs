using Shared.Lighting;

namespace Api;

/// <summary>Свет в точке мира (<see cref="IWorld.GetLight"/>): небесный и блочный, 0–15 каждый.</summary>
/// <param name="Sky">
/// Свет неба: 15 — под открытым небом, меньше — в тени и под землёй. От времени суток не зависит: ночью он тот же, а
/// темнее мир рисует клиент (<see cref="WorldTime.SkyBrightness"/>).
/// </param>
/// <param name="Block">Свет от светящихся блоков (факел — 15, гаснет на 1 за блок).</param>
public readonly record struct LightLevel(int Sky, int Block)
{
    /// <summary>
    /// Насколько светло сейчас, 0–15: блочный свет или небесный с поправкой на время суток — что больше. Так считает и
    /// клиент, когда рисует мир.
    /// </summary>
    public float Effective(WorldTime time) => MathF.Max(Sky * time.SkyBrightness, Block);
}

/// <summary>Время мира (<see cref="IWorld.Time"/>): тики с создания мира и что из них следует для суток.</summary>
/// <param name="Ticks">Тик мира: растёт с создания мира по 20 в секунду и в полночь не сбрасывается.</param>
public readonly record struct WorldTime(long Ticks)
{
    /// <summary>Доля суток от начала рассвета, от 0 до 1: день — первые 3/4 (30 минут), ночь — последняя четверть (10).</summary>
    public double DayFraction => DayCycle.TimeOfDay(Ticks) / DayCycle.LengthTicks;

    public bool IsDay => DayCycle.IsDay(Ticks);

    /// <summary>Яркость неба — множитель небесного света: 1 днём, 4/15 ночью, плавно на рассвете и закате.</summary>
    public float SkyBrightness => DayCycle.SkyBrightness(Ticks);

    /// <summary>Как в заголовке окна: «день 4:10 из 30:00».</summary>
    public override string ToString() => DayCycle.Describe(Ticks);
}
