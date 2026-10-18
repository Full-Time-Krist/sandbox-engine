using Client.Ui;

namespace Client.Tests.Ui;

public class ToastQueueTests
{
    private readonly ToastQueue<string> _queue = new(seconds: 4, fadeSeconds: 0.5);

    private string? Advance(double seconds) => _queue.TryAdvance(seconds, out var item, out _) ? item : null;

    [Fact]
    public void Queued_ShownOneAfterAnother_EachForItsFullTime()
    {
        _queue.Show("a");
        Assert.Equal("a", Advance(1));
        _queue.Show("b"); // пока показывается первое: ждёт своей очереди, а не сбивает время первому

        // a — до 4-й секунды, b — следующие 4, дальше пусто.
        string?[] shown = [Advance(1), Advance(1), Advance(1), Advance(1), Advance(1), Advance(1), Advance(1), Advance(1)];

        Assert.Equal(["a", "a", "b", "b", "b", "b", null, null], shown);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void LastHalfSecond_Fades()
    {
        _queue.Show("a");

        Assert.True(_queue.TryAdvance(3, out _, out var full));
        Assert.True(_queue.TryAdvance(0.75, out _, out var half));

        Assert.Equal(1f, full);
        Assert.Equal(0.5f, half, 3);
    }

    [Fact]
    public void LongFrame_SkipsFinished_ShowsCurrentWithItsRemainingTime()
    {
        _queue.Show("a");
        _queue.Show("b");
        _queue.Show("c");

        Assert.Equal("c", Advance(9)); // a кончилось в 4, b — в 8
        Assert.Equal("c", Advance(2.9));
        Assert.Null(Advance(0.2));
    }

    [Fact]
    public void AfterEmpty_NewToastGetsFullTime()
    {
        _queue.Show("a");
        Assert.Null(Advance(10));

        _queue.Show("b");
        Assert.Equal("b", Advance(3.9));
        Assert.Null(Advance(0.2));
    }
}
