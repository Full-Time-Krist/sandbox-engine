namespace Shared;

/// <summary>Частота симуляции. Общая для клиента и сервера, иначе интерполяция разъедется.</summary>
public static class GameTime
{
    public const int TicksPerSecond = 20;
    public const float TickDuration = 1f / TicksPerSecond;
}
