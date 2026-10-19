namespace Client;

/// <summary>
/// Повтор действия, пока кнопку держат: при нажатии — сразу, потом раз в <paramref name="interval"/> секунд.
/// Без ввода и окна — проверяется тестами.
/// </summary>
/// <remarks>
/// Повтор идёт только от нажатия, которое класс видел. Кнопку, зажатую раньше (кликом захватили курсор, держали её
/// в инвентаре), он не подхватывает: иначе закрытие экрана с зажатой кнопкой сразу ломало бы или ставило блок.
/// </remarks>
internal sealed class ButtonRepeat(double interval)
{
    private double? _next;

    /// <summary>Кадр: нажата ли кнопка в этом кадре, держат ли её, текущее время. Возвращает, выполнить ли действие.</summary>
    public bool Update(bool pressed, bool down, double now)
    {
        if (pressed)
        {
            _next = now + interval;
            return true;
        }

        if (!down || _next is not { } next)
        {
            _next = null;
            return false;
        }

        if (now < next)
            return false;

        // Отсчёт от текущего кадра: после долгого кадра (окно тащили) — одно действие, а не пачка догоняющих.
        _next = now + interval;
        return true;
    }

    /// <summary>Забыть нажатие: действия запрещены (открыт экран, отпущен курсор) — повтор не продолжится.</summary>
    public void Reset() => _next = null;
}
