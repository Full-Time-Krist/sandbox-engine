namespace Client.Tests;

public class ButtonRepeatTests
{
    private readonly ButtonRepeat _repeat = new(interval: 0.2);

    [Fact]
    public void Press_ActsAtOnce_ThenRepeatsEveryIntervalWhileHeld()
    {
        Assert.True(_repeat.Update(pressed: true, down: true, now: 1.0));
        Assert.False(_repeat.Update(pressed: false, down: true, now: 1.1));
        Assert.True(_repeat.Update(pressed: false, down: true, now: 1.2));
        Assert.False(_repeat.Update(pressed: false, down: true, now: 1.3));
        Assert.True(_repeat.Update(pressed: false, down: true, now: 1.4));
    }

    [Fact]
    public void Release_StopsRepeat()
    {
        _repeat.Update(pressed: true, down: true, now: 1.0);
        Assert.False(_repeat.Update(pressed: false, down: false, now: 1.1));
        Assert.False(_repeat.Update(pressed: false, down: true, now: 1.5));
    }

    [Fact]
    public void ButtonHeldWithoutSeenPress_DoesNotAct()
    {
        Assert.False(_repeat.Update(pressed: false, down: true, now: 1.0));
        Assert.False(_repeat.Update(pressed: false, down: true, now: 5.0));
    }

    [Fact]
    public void Reset_StopsRepeat_UntilNextPress()
    {
        _repeat.Update(pressed: true, down: true, now: 1.0);
        _repeat.Reset();
        Assert.False(_repeat.Update(pressed: false, down: true, now: 2.0));
        Assert.True(_repeat.Update(pressed: true, down: true, now: 2.1));
    }

    [Fact]
    public void LongFrame_GivesOneAction_NotCatchUpBurst()
    {
        _repeat.Update(pressed: true, down: true, now: 1.0);
        Assert.True(_repeat.Update(pressed: false, down: true, now: 3.0));
        Assert.False(_repeat.Update(pressed: false, down: true, now: 3.01));
        Assert.True(_repeat.Update(pressed: false, down: true, now: 3.2));
    }

    [Fact]
    public void ShortClick_PressedAndReleasedInOneFrame_ActsOnce()
    {
        Assert.True(_repeat.Update(pressed: true, down: false, now: 1.0));
        Assert.False(_repeat.Update(pressed: false, down: false, now: 1.3));
    }
}
