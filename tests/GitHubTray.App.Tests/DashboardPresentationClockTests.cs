using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class DashboardPresentationClockTests
{
    [Fact]
    public void HiddenInitializationAndQueuedTicksDoNoPresentationWork()
    {
        var starts = 0;
        var stops = 0;
        var updates = 0;
        var clock = new DashboardPresentationClock(() => starts++, () => stops++, () => updates++);

        clock.Initialize();
        for (var minute = 0; minute < 480; minute++)
            clock.Tick();

        Assert.Equal(0, starts);
        Assert.Equal(0, stops);
        Assert.Equal(0, updates);

        clock.SetVisible(true);
        Assert.Equal(1, starts);
        Assert.Equal(1, updates); // Catch up at reveal, not at the next tick.
        clock.Tick();
        Assert.Equal(2, updates);
    }

    [Fact]
    public void RepeatedVisibilityCallsAreIdempotentAndRevealCatchesUpBeforeStarting()
    {
        var events = new List<string>();
        var clock = new DashboardPresentationClock(
            () => events.Add("start"), () => events.Add("stop"), () => events.Add("update"));
        clock.SetVisible(true);
        Assert.Empty(events);
        clock.Initialize();
        clock.Initialize();
        clock.SetVisible(true);
        Assert.Equal(["update", "start"], events);

        clock.SetVisible(false);
        clock.SetVisible(false);
        clock.Tick();
        clock.SetVisible(true);
        clock.SetVisible(true);

        Assert.Equal(["update", "start", "stop", "update", "start"], events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisibleInitializationCatchesUpAgedStartupDataBeforeStarting(bool isRevealedWhileSettingsLoad)
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var displayedAt = now; // The independent initial refresh has already been projected.
        var events = new List<string>();
        var clock = new DashboardPresentationClock(
            () => events.Add("start"),
            () => events.Add("stop"),
            () =>
            {
                displayedAt = now;
                events.Add("update");
            });
        clock.SetVisible(true);
        now = now.AddMinutes(10);
        if (isRevealedWhileSettingsLoad)
        {
            clock.SetVisible(false);
            clock.SetVisible(true);
        }
        Assert.Empty(events);
        Assert.NotEqual(now, displayedAt);

        clock.Initialize(); // Settings finally finish while the panel is visible.
        clock.Initialize();
        clock.SetVisible(true);

        Assert.Equal(now, displayedAt);
        Assert.Equal(["update", "start"], events);
    }

    [Fact]
    public void HidingBeforeSettingsFinishDoesNotRestartTheClockAtInitialization()
    {
        var events = new List<string>();
        var clock = new DashboardPresentationClock(
            () => events.Add("start"), () => events.Add("stop"), () => events.Add("update"));
        clock.SetVisible(true);
        clock.SetVisible(false);
        clock.Initialize();
        clock.Tick();

        Assert.Empty(events);
        clock.SetVisible(true);
        Assert.Equal(["update", "start"], events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShutdownIsTerminalEvenWhenInitializationOrTicksArriveLater(bool wasInitialized)
    {
        var starts = 0;
        var stops = 0;
        var updates = 0;
        var clock = new DashboardPresentationClock(() => starts++, () => stops++, () => updates++);
        clock.SetVisible(true);
        if (wasInitialized)
            clock.Initialize();
        clock.Stop();
        clock.Stop();
        clock.Initialize();
        clock.SetVisible(false);
        clock.SetVisible(true);
        clock.Tick();

        Assert.Equal(wasInitialized ? 1 : 0, starts);
        Assert.Equal(wasInitialized ? 1 : 0, stops);
        Assert.Equal(wasInitialized ? 1 : 0, updates);
    }
}
