using Shared;

namespace Server.Entities;

/// <summary>Здоровье игрока: единицы здоровья, восстановление по таймеру.</summary>
internal struct Health
{
    public int Current;
    public int Max;

    /// <summary>Тиков с последнего восстановления (или с урона).</summary>
    public int RegenTicks;
}

/// <summary>Падение: самая высокая точка ног с последнего касания земли — по ней считается урон при приземлении.</summary>
internal struct Fall
{
    public double HighestY;
}

/// <summary>Правила здоровья игрока: сколько его, как падение ранит и как оно восстанавливается.</summary>
internal static class PlayerHealth
{
    /// <summary>20 единиц — на полосе над хотбаром по 4 пикселя интерфейса на единицу.</summary>
    public const int Max = 20;

    /// <summary>С какой высоты падение безопасно, в блоках: дальше — по единице за каждый целый блок.</summary>
    public const int SafeFall = 3;

    /// <summary>Восстановление — единица за 4 с, пока голода нет.</summary>
    public const int RegenIntervalTicks = 4 * GameTime.TicksPerSecond;

    /// <summary>Причина урона от падения — имя, как у контента: моды различают причины по нему.</summary>
    public const string FallCause = "base:fall";

    /// <summary>Урон от падения с высоты <paramref name="distance"/>: 3 блока — ноль, 10 — семь.</summary>
    public static int FallDamage(double distance) => Math.Max(0, (int)Math.Floor(distance + 1e-6) - SafeFall);
}
