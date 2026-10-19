namespace Shared.Lighting;

/// <summary>
/// Сутки мира: 30 минут дня и 10 минут ночи (выбор пользователя), время — счётчик тиков мира на сервере. Одни правила
/// для сервера (позже — появление мобов, рост) и клиента (яркость неба, цвет неба, солнце и луна).
/// </summary>
/// <remarks>
/// Время суток — тики от начала рассвета: <c>[0; <see cref="DayTicks"/>)</c> — день, дальше ночь. Рассвет и закат —
/// по минуте на границах дня (в его начале и конце): яркость неба плавно идёт между ночной и полной. Солнце встаёт
/// на востоке с началом дня, к полудню — в зените, садится на западе с концом дня; ночью по небу идёт луна.
/// </remarks>
public static class DayCycle
{
    /// <summary>Длина суток: 40 минут.</summary>
    public const int LengthTicks = 40 * 60 * GameTime.TicksPerSecond;

    /// <summary>День, вместе с рассветом и закатом: 30 минут.</summary>
    public const int DayTicks = 30 * 60 * GameTime.TicksPerSecond;

    public const int NightTicks = LengthTicks - DayTicks;

    /// <summary>Рассвет и закат — по минуте.</summary>
    public const int TransitionTicks = 60 * GameTime.TicksPerSecond;

    /// <summary>«Час» — 1/24 суток (100 секунд): на столько переводит время клавиша T.</summary>
    public const int HourTicks = LengthTicks / 24;

    /// <summary>Яркость неба ночью: небесный свет 15 светит как 4.</summary>
    public const float NightSkyBrightness = 4f / 15;

    /// <summary>Время нового мира: утро, рассвет только что кончился.</summary>
    public const long NewWorldTime = TransitionTicks;

    /// <summary>Время суток — тики от начала рассвета, от 0 до <see cref="LengthTicks"/>. Дробное — для плавности у клиента.</summary>
    public static double TimeOfDay(double worldTime)
    {
        var t = worldTime % LengthTicks;
        return t < 0 ? t + LengthTicks : t;
    }

    public static bool IsDay(double worldTime) => TimeOfDay(worldTime) < DayTicks;

    /// <summary>Яркость неба — множитель небесного света: 1 днём, <see cref="NightSkyBrightness"/> ночью, плавно на рассвете и закате.</summary>
    public static float SkyBrightness(double worldTime)
    {
        var t = TimeOfDay(worldTime);
        if (t < TransitionTicks)
            return Mix(NightSkyBrightness, 1, Smooth(t / TransitionTicks));
        if (t < DayTicks - TransitionTicks)
            return 1;
        if (t < DayTicks)
            return Mix(1, NightSkyBrightness, Smooth((t - (DayTicks - TransitionTicks)) / TransitionTicks));
        return NightSkyBrightness;
    }

    /// <summary>
    /// Угол солнца над восточным горизонтом, радианы: 0 — восход, π/2 — зенит в полдень, π — закат; ночью от π до 2π
    /// (под землёй). Луна — напротив солнца.
    /// </summary>
    public static double SunAngle(double worldTime)
    {
        var t = TimeOfDay(worldTime);
        return t < DayTicks ? Math.PI * t / DayTicks : Math.PI + Math.PI * (t - DayTicks) / NightTicks;
    }

    /// <summary>Для заголовка окна: «день 4:10 из 30:00» — сколько минут прошло дня или ночи.</summary>
    public static string Describe(double worldTime)
    {
        var t = TimeOfDay(worldTime);
        var (name, elapsed, length) = t < DayTicks ? ("день", t, DayTicks) : ("ночь", t - DayTicks, NightTicks);
        return $"{name} {Clock(elapsed)} из {Clock(length)}";

        static string Clock(double ticks)
        {
            var seconds = (int)(ticks / GameTime.TicksPerSecond);
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }

    private static float Smooth(double x) => (float)(x * x * (3 - 2 * x));

    private static float Mix(float a, float b, float k) => a + (b - a) * k;
}
