using System.Runtime.CompilerServices;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class DashboardStateProjectionTests
{
    [Fact]
    public void HiddenPublicationDoesNotProjectUntilAuthoritativeReveal()
    {
        var projected = new List<object>();
        var projection = new DashboardStateProjection<object>(projected.Add, () => { });
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
        }, () => { });
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
        var releases = 0;
        var projection = new DashboardStateProjection<object>(projected.Add, () => releases++);
        var state = new object();

        projection.Reveal(() => state);
        projection.Reveal(() => state);
        projection.Publish(state);
        projection.Hide();
        projection.Hide();
        projection.Reveal(() => state);

        Assert.Equal([state, state], projected);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void StopProjectsTerminalStateWhileHiddenAndRejectsLaterWork()
    {
        var projected = new List<object>();
        var releases = 0;
        var projection = new DashboardStateProjection<object>(projected.Add, () => releases++);
        var visible = new object();
        var hidden = new object();
        var terminal = new object();

        projection.Reveal(() => visible);
        projection.Hide();
        projection.Publish(hidden);
        projection.Stop(terminal);
        projection.Publish(new object());
        projection.Reveal(() => new object());
        projection.Hide();

        Assert.Equal([visible, terminal], projected);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void HideAndHiddenPublicationsDoNotRetainObsoleteState()
    {
        object? displayed = null;
        var projection = new DashboardStateProjection<object>(
            state => displayed = state,
            () => displayed = null);
        var visible = PublishUnownedState(projection, visible: true);
        projection.Hide();
        var hidden = PublishUnownedState(projection, visible: false);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Null(displayed);
        Assert.False(visible.IsAlive);
        Assert.False(hidden.IsAlive);
        GC.KeepAlive(projection);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PublishUnownedState(
        DashboardStateProjection<object> projection, bool visible)
    {
        var state = new object();
        if (visible)
        {
            projection.Reveal(() => state);
        }
        else
        {
            projection.Publish(state);
        }
        return new(state);
    }
}
