namespace Client.Ui;

/// <summary>
/// Очередь всплывающих сообщений: показывается первое, каждое — <see cref="Seconds"/> секунд, последние
/// <see cref="FadeSeconds"/> гаснет; следующее начинается, когда кончилось предыдущее. Без отрисовки — проверяется тестами.
/// </summary>
internal sealed class ToastQueue<T>(double seconds, double fadeSeconds)
{
    private readonly Queue<T> _queue = new();
    private double _timeLeft;

    public double Seconds => seconds;
    public double FadeSeconds => fadeSeconds;
    public int Count => _queue.Count;

    public void Show(T item)
    {
        if (_queue.Count == 0)
            _timeLeft = seconds;
        _queue.Enqueue(item);
    }

    /// <summary>Проходит <paramref name="deltaTime"/> секунд. Возвращает, что показывать сейчас и с какой непрозрачностью.</summary>
    public bool TryAdvance(double deltaTime, out T current, out float alpha)
    {
        _timeLeft -= deltaTime;
        // Долгий кадр (окно тащили) может закончить сразу несколько сообщений: их не показываем мельком.
        while (_queue.Count > 0 && _timeLeft <= 0)
        {
            _queue.Dequeue();
            _timeLeft += _queue.Count > 0 ? seconds : 0;
        }

        if (_queue.Count == 0)
        {
            current = default!;
            alpha = 0;
            return false;
        }
        current = _queue.Peek();
        alpha = (float)Math.Min(1, _timeLeft / fadeSeconds);
        return true;
    }
}
