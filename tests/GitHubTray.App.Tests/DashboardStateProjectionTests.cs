using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class DashboardStateProjectionTests
{
    [Fact]
    public void HiddenPublicationKeepsOnlyLatestStateUntilReveal()
    {
        var projected = new List<object>();
        var projection = new DashboardStateProjection<object>(projected.Add);
        var first = new object();
        var second = new object();

        projection.Publish(first);
        projection.Publish(second);

        Assert.Empty(projected);
        projection.Reveal(() => second);
        Assert.Equal([second], projected);
    }

    [Fact]
    public void RevealReadsAuthoritativeStateBeforeProjectingInsteadOfUsingQueuedLatest()
    {
        var queued = new object();
        var current = new object();
        var authoritativeRead = false;
        var projected = new List<object>();
        var projection = new DashboardStateProjection<object>(state =>
        {
            Assert.True(authoritativeRead);
            projected.Add(state);
        });
        projection.Publish(queued);

        projection.Reveal(() =>
        {
            authoritativeRead = true;
            return current;
        });

        Assert.Equal([current], projected);
    }

    [Fact]
    public void VisiblePublicationIsReferenceIdempotentAndRevealReprojectsLatest()
    {
        var projected = new List<object>();
        var projection = new DashboardStateProjection<object>(projected.Add);
        var state = new object();

        projection.Reveal(() => state);
        projection.Publish(state);
        projection.Hide();
        projection.Reveal(() => state);

        Assert.Equal([state, state], projected);
    }

    [Fact]
    public void StopProjectsTerminalStateWhileHiddenAndRejectsLaterWork()
    {
        var projected = new List<object>();
        var projection = new DashboardStateProjection<object>(projected.Add);
        var visible = new object();
        var hidden = new object();
        var terminal = new object();

        projection.Reveal(() => visible);
        projection.Hide();
        projection.Publish(hidden);
        projection.Stop(terminal);
        projection.Publish(new object());
        projection.Reveal(() => new object());

        Assert.Equal([visible, terminal], projected);
    }
}
