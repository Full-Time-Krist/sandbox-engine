namespace Engine.Timing;

/// <summary>Считает кадры и тики за последнюю секунду.</summary>
public sealed class FrameStats
{
    private double _elapsed;
    private int _frames;
    private int _ticks;

    public int Fps { get; private set; }
    public int Tps { get; private set; }

    /// <summary>Возвращает true, когда значения обновились (раз в секунду).</summary>
    public bool Record(double frameTime, int ticks)
    {
        _elapsed += frameTime;
        _frames++;
        _ticks += ticks;

        if (_elapsed < 1.0)
            return false;

        Fps = (int)Math.Round(_frames / _elapsed);
        Tps = (int)Math.Round(_ticks / _elapsed);
        _elapsed = 0;
        _frames = 0;
        _ticks = 0;
        return true;
    }
}
