using GitHubTray.Core;

namespace GitHubTray.AppState.Tests;

public sealed class DashboardRefreshSessionLifecycleTests
{
    [Fact]
    public async Task InitialLoadingAndVerifiedSuccessArePublishedAsCoherentStates()
    {
        await using var fixture = new RefreshSessionFixture();
        var session = fixture.Session;
        var initial = session.State;
        Assert.Null(initial.Snapshot);
        Assert.Null(initial.LastKnownLogin);
        Assert.Null(initial.Error);
        Assert.False(initial.IsRefreshing);
        Assert.False(initial.IsStopping);
        Assert.False(initial.IsAccountVerified);
        Assert.Empty(fixture.Api.Requests);

        var identity = fixture.Gate();
        var finalIdentity = fixture.Gate();
        var responses = RefreshResponses.Success();
        responses.InitialUser = responses.InitialUser with { Gate = identity };
        responses.FinalUser = responses.FinalUser with { Gate = finalIdentity };
        fixture.Api.Use(responses);

        var refresh = session.RefreshAsync();
        var loading = session.State;
        Assert.True(loading.IsRefreshing);
        Assert.False(loading.IsStopping);
        Assert.False(loading.IsAccountVerified);
        Assert.Null(loading.Snapshot);
        Assert.Null(loading.LastKnownLogin);
        Assert.Null(loading.Error);
        Assert.False(refresh.IsCompleted);

        await identity.EnteredAsync();
        Assert.Equal(ApiRoute.InitialUser, Assert.Single(fixture.Api.Requests).Route);
        identity.Release();
        await finalIdentity.EnteredAsync();
        Assert.True(session.State.IsRefreshing);
        Assert.False(session.State.IsAccountVerified);
        Assert.Null(session.State.Snapshot);
        Assert.Equal(8, fixture.Api.Requests.Length);
        Assert.Equal(ApiRoute.FinalUser, fixture.Api.Requests[^1].Route);
        finalIdentity.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(session.State);
        Assert.Contains(fixture.Api.Requests, request => request.Target.Contains("author:octocat", StringComparison.Ordinal));
        Assert.Contains(fixture.Api.Requests, request => request.Target.Contains("review-requested:octocat", StringComparison.Ordinal));
        Assert.False(initial.IsRefreshing);
        Assert.Null(initial.Snapshot);
        Assert.True(loading.IsRefreshing);
        Assert.Null(loading.Snapshot);
        Assert.Null(loading.LastKnownLogin);
    }

    [Fact]
    public async Task ConcurrentTriggersShareThePendingTaskAndOnlyOneTransportRound()
    {
        await using var fixture = new RefreshSessionFixture();
        var gate = fixture.Gate();
        var responses = RefreshResponses.Success();
        responses.InitialUser = responses.InitialUser with { Gate = gate };
        fixture.Api.Use(responses);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenders = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            await start.Task.WaitAsync(RefreshSessionFixture.Timeout);
            return (Refresh: fixture.Session.RefreshAsync(), Loading: fixture.Session.State.IsRefreshing);
        })).ToArray();
        start.TrySetResult();
        var calls = await Task.WhenAll(contenders).WaitAsync(RefreshSessionFixture.Timeout);
        await gate.EnteredAsync();
        var first = calls[0].Refresh;

        Assert.All(calls, call =>
        {
            Assert.Same(first, call.Refresh);
            Assert.True(call.Loading);
        });
        Assert.Same(first, fixture.Session.RefreshAsync());
        Assert.Single(fixture.Api.Requests);
        gate.Release();
        await first.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(fixture.Session.State);
        Assert.Equal(8, fixture.Api.Requests.Length);
        Assert.Equal(8, fixture.Api.Requests.Select(request => request.Route).Distinct().Count());
    }

    [Fact]
    public async Task ReentrantTriggersDuringSynchronousTransportReturnTheAlreadyPublishedActiveTask()
    {
        await using var fixture = new RefreshSessionFixture();
        var nested = new List<Task>();
        var responses = RefreshResponses.Success();
        void TriggerAgain()
        {
            Assert.True(fixture.Session.State.IsRefreshing);
            nested.Add(fixture.Session.RefreshAsync());
        }
        responses.InitialUser = responses.InitialUser with { BeforeSend = TriggerAgain };
        responses.FinalUser = responses.FinalUser with { BeforeSend = TriggerAgain };
        fixture.Api.Use(responses);

        var refresh = fixture.Session.RefreshAsync();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        Assert.Equal(2, nested.Count);
        Assert.All(nested, task => Assert.Same(refresh, task));
        SessionAssertions.Success(fixture.Session.State);
        Assert.Equal(8, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task SynchronouslyCompletedTransportDoesNotLeaveAnActiveTaskOrStrandLaterRefreshes()
    {
        await using var fixture = new RefreshSessionFixture();
        for (var round = 0; round < 3; round++)
        {
            var revision = $"revision-{round}";
            fixture.Api.Use(RefreshResponses.Success(revision: revision));
            var refresh = fixture.Session.RefreshAsync();
            await refresh.WaitAsync(RefreshSessionFixture.Timeout);

            SessionAssertions.Success(fixture.Session.State, revision: revision);
            Assert.Equal((round + 1) * 8, fixture.Api.Requests.Length);
        }

        var gate = fixture.Gate();
        var next = RefreshResponses.Success(revision: "gated");
        next.InitialUser = next.InitialUser with { Gate = gate };
        fixture.Api.Use(next);
        var pending = fixture.Session.RefreshAsync();
        Assert.True(fixture.Session.State.IsRefreshing);
        await gate.EnteredAsync();
        Assert.False(pending.IsCompleted);
        Assert.Same(pending, fixture.Session.RefreshAsync());
        Assert.Equal(25, fixture.Api.Requests.Length);
        gate.Release();
        await pending.WaitAsync(RefreshSessionFixture.Timeout);
        SessionAssertions.Success(fixture.Session.State, revision: "gated");
        Assert.Equal(32, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task OrdinaryRefreshKeepsVerifiedDataVisibleUntilTheReplacementIsVerified()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var previous = fixture.Session.State;
        SessionAssertions.Success(previous);
        var gate = fixture.Gate();
        var responses = RefreshResponses.Success(revision: "replacement");
        responses.FinalUser = responses.FinalUser with { Gate = gate };
        fixture.Api.Use(responses);

        var refresh = fixture.Session.RefreshAsync();
        Assert.True(fixture.Session.State.IsRefreshing);
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.Equal("first", Assert.Single(fixture.Session.State.Snapshot!.Activity.Items).Id);
        await gate.EnteredAsync();
        var loading = fixture.Session.State;
        Assert.True(loading.IsRefreshing);
        Assert.True(loading.IsAccountVerified);
        Assert.Equal("first", Assert.Single(loading.Snapshot!.Activity.Items).Id);
        Assert.Equal("octocat/first", Assert.Single(loading.Snapshot.Repositories.Items).Title);
        Assert.NotNull(loading.Snapshot.Contributions.Calendar);
        Assert.Equal("octocat", loading.LastKnownLogin);
        Assert.Null(loading.Error);

        gate.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);
        SessionAssertions.Success(fixture.Session.State, revision: "replacement");
        SessionAssertions.Success(previous);
        Assert.True(loading.IsRefreshing);
        Assert.Equal("first", Assert.Single(loading.Snapshot.Activity.Items).Id);
    }

    [Fact]
    public async Task ShutdownDrainsCancellationIgnoringTransportAndRejectsLatePublication()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var published = fixture.Session.State;
        var gate = fixture.Gate(ignoreCancellation: true);
        var responses = RefreshResponses.Success(revision: "must-not-publish");
        responses.FinalUser = responses.FinalUser with { Gate = gate };
        fixture.Api.Use(responses);
        var refresh = fixture.Session.RefreshAsync();
        await gate.EnteredAsync();
        var callsBeforeShutdown = fixture.Api.Requests.Length;

        var shutdown = fixture.Session.ShutdownAsync();
        AssertStoppedState(fixture.Session.State, "octocat");
        Assert.False(shutdown.IsCompleted);
        Assert.Same(shutdown, fixture.Session.ShutdownAsync());
        await gate.CanceledAsync();
        Assert.False(shutdown.IsCompleted);
        Assert.False(refresh.IsCompleted);

        var ignored = fixture.Session.RefreshAsync();
        Assert.True(ignored.IsCompletedSuccessfully);
        await ignored.WaitAsync(RefreshSessionFixture.Timeout);
        Assert.Equal(callsBeforeShutdown, fixture.Api.Requests.Length);

        gate.Release();
        await shutdown.WaitAsync(RefreshSessionFixture.Timeout);
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);
        Assert.True(refresh.IsCompletedSuccessfully);
        await gate.ExitedAsync();
        AssertStoppedState(fixture.Session.State, "octocat");
        Assert.Same(shutdown, fixture.Session.ShutdownAsync());
        await fixture.Session.DisposeAsync().AsTask().WaitAsync(RefreshSessionFixture.Timeout);
        await fixture.Session.DisposeAsync().AsTask().WaitAsync(RefreshSessionFixture.Timeout);
        Assert.True(fixture.Session.RefreshAsync().IsCompletedSuccessfully);
        Assert.Equal(callsBeforeShutdown, fixture.Api.Requests.Length);
        SessionAssertions.Success(published);
    }

    [Fact]
    public async Task ShutdownCancellationDoesNotBecomeAnAuthenticationOrOfflineError()
    {
        await using var fixture = new RefreshSessionFixture();
        var gate = fixture.Gate();
        var responses = RefreshResponses.Success();
        responses.InitialUser = responses.InitialUser with { Gate = gate };
        fixture.Api.Use(responses);
        var refresh = fixture.Session.RefreshAsync();
        await gate.EnteredAsync();

        var shutdown = fixture.Session.ShutdownAsync();
        AssertStoppedState(fixture.Session.State, null);
        await gate.CanceledAsync();
        await shutdown.WaitAsync(RefreshSessionFixture.Timeout);
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);
        Assert.True(refresh.IsCompletedSuccessfully);
        await gate.ExitedAsync();

        AssertStoppedState(fixture.Session.State, null);
        Assert.Single(fixture.Api.Requests);
        Assert.True(fixture.Session.RefreshAsync().IsCompletedSuccessfully);
        Assert.Single(fixture.Api.Requests);
    }

    [Fact]
    public async Task IdleShutdownAndDisposeAreIdempotentAndNeverStartTransport()
    {
        await using var fixture = new RefreshSessionFixture();
        var initial = fixture.Session.State;
        var shutdown = fixture.Session.ShutdownAsync();
        AssertStoppedState(fixture.Session.State, null);
        Assert.Same(shutdown, fixture.Session.ShutdownAsync());
        await shutdown.WaitAsync(RefreshSessionFixture.Timeout);
        await fixture.Session.DisposeAsync().AsTask().WaitAsync(RefreshSessionFixture.Timeout);
        await fixture.Session.DisposeAsync().AsTask().WaitAsync(RefreshSessionFixture.Timeout);

        Assert.True(fixture.Session.RefreshAsync().IsCompletedSuccessfully);
        AssertStoppedState(fixture.Session.State, null);
        Assert.Empty(fixture.Api.Requests);
        Assert.False(initial.IsStopping);
    }

    [Fact]
    public async Task DisposeAloneCancelsAndDrainsActiveRefresh()
    {
        await using var fixture = new RefreshSessionFixture();
        var gate = fixture.Gate(ignoreCancellation: true);
        var responses = RefreshResponses.Success();
        responses.FinalUser = responses.FinalUser with { Gate = gate };
        fixture.Api.Use(responses);
        var refresh = fixture.Session.RefreshAsync();
        await gate.EnteredAsync();

        var disposal = fixture.Session.DisposeAsync().AsTask();
        AssertStoppedState(fixture.Session.State, null);
        await gate.CanceledAsync();
        Assert.False(disposal.IsCompleted);
        gate.Release();
        await disposal.WaitAsync(RefreshSessionFixture.Timeout);
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);
        Assert.True(refresh.IsCompletedSuccessfully);
        AssertStoppedState(fixture.Session.State, null);
        Assert.True(fixture.Session.RefreshAsync().IsCompletedSuccessfully);
        Assert.Equal(8, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task ShutdownAfterIdentityFailureClearsTheErrorAndRetainsOnlyLastKnownLogin()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var outage = RefreshResponses.Success();
        outage.InitialUser = outage.InitialUser with { Failure = new GitHubException("Signed out") };
        fixture.Api.Use(outage);
        await fixture.RefreshAsync();
        var failed = fixture.Session.State;
        SessionAssertions.Unverified(failed, "octocat");
        Assert.Equal("Signed out", failed.Error);

        var shutdown = fixture.Session.ShutdownAsync();
        AssertStoppedState(fixture.Session.State, "octocat");
        await shutdown.WaitAsync(RefreshSessionFixture.Timeout);

        AssertStoppedState(fixture.Session.State, "octocat");
        Assert.True(fixture.Session.RefreshAsync().IsCompletedSuccessfully);
        Assert.Equal(9, fixture.Api.Requests.Length);
        SessionAssertions.Unverified(failed, "octocat");
        Assert.Equal("Signed out", failed.Error);
    }

    private static void AssertStoppedState(DashboardSessionState state, string? lastKnownLogin)
    {
        Assert.True(state.IsStopping);
        Assert.False(state.IsRefreshing);
        Assert.False(state.IsAccountVerified);
        Assert.Null(state.Snapshot);
        Assert.Null(state.Error);
        Assert.Equal(lastKnownLogin, state.LastKnownLogin);
    }
}
