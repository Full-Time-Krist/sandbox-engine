namespace Client;

/// <summary>
/// Часы мира у клиента: тик мира от сервера (<see cref="Shared.Protocol.JoinAccepted"/>, <see cref="Shared.Protocol.TimeSync"/>),
/// между сверками клиент прибавляет по тику сам — с той же частотой, что сервер. Сверка ставит часы точно.
/// </summary>
internal sealed class WorldClock
{
    private long _synced;
    private long _ticksSinceSync;

    /// <summary>Пришло ли время от сервера. До него часов нет — и тикать нечего.</summary>
    public bool IsKnown { get; private set; }

    /// <summary>Тик мира сейчас.</summary>
    public long Now => _synced + _ticksSinceSync;

    public void Sync(long worldTime)
    {
        _synced = worldTime;
        _ticksSinceSync = 0;
        IsKnown = true;
    }

    /// <summary>
    /// Прошёл тик клиента. Звать до разбора сообщений тика: сверка, пришедшая в нём, уже учитывает этот тик.
    /// Тики до первой сверки не важны — она обнуляет счёт.
    /// </summary>
    public void Tick() => _ticksSinceSync++;

    /// <summary>Время для кадра: между тиками — с долей пройденного тика, чтобы солнце шло плавно.</summary>
    public double At(float alpha) => Now + alpha;
}
