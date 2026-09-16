using GitHubTray.Core;

namespace GitHubTray.AppState.Tests;

public sealed class DashboardRefreshSessionFreshnessTests
{
    [Fact]
    public async Task InjectedAllFreshStartupSnapshotUsesOnlyIdentityAndKeepsOriginalTimestamps()
    {
        var clock = new MutableTimeProvider();
        DashboardSnapshot seed;
        await using (var source = new RefreshSessionFixture(clock: clock))
        {
            await source.RefreshAsync();
            seed = SessionAssertions.Success(source.Session.State);
        }
        clock.Advance(TimeSpan.FromMinutes(4));

        await using var fixture = new RefreshSessionFixture(
            seed,
            TimeSpan.FromMinutes(5),
            clock);
        fixture.Api.Use(RefreshResponses.Success(revision: "must-not-load"));

        await fixture.RefreshAsync(DashboardRefreshReason.Startup);

        var snapshot = SessionAssertions.Success(fixture.Session.State);
        Assert.Equal(
            [ApiRoute.InitialUser, ApiRoute.FinalUser],
            fixture.Api.Requests.Select(request => request.Route));
        Assert.Equal(seed.Activity.UpdatedAt, snapshot.Activity.UpdatedAt);
        Assert.Equal(seed.PullRequests.UpdatedAt, snapshot.PullRequests.UpdatedAt);
        Assert.Equal(seed.ReviewRequests.UpdatedAt, snapshot.ReviewRequests.UpdatedAt);
        Assert.Equal(seed.Repositories.UpdatedAt, snapshot.Repositories.UpdatedAt);
        Assert.Equal(seed.Contributions.UpdatedAt, snapshot.Contributions.UpdatedAt);
        Assert.Equal(seed.Copilot.UpdatedAt, snapshot.Copilot.UpdatedAt);
    }

    [Fact]
    public async Task PeriodicRefreshUsesFiveOperationsWhileFreshAndNextTickIsNotDeferred()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var firstPeriodic = RefreshResponses.Success(revision: "periodic-one");
        var finalIdentity = fixture.Gate();
        firstPeriodic.FinalUser = firstPeriodic.FinalUser with { Gate = finalIdentity };
        fixture.Api.Use(firstPeriodic);
        var before = fixture.Api.Requests.Length;

        var first = fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic);
        await finalIdentity.EnteredAsync();
        Assert.Same(first, fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic));
        Assert.Equal(5, fixture.Api.Requests.Length - before);
        finalIdentity.Release();
        await first.WaitAsync(RefreshSessionFixture.Timeout);

        fixture.Api.Use(RefreshResponses.Success(revision: "periodic-two"));
        before = fixture.Api.Requests.Length;
        await fixture.RefreshAsync(DashboardRefreshReason.Periodic);

        var snapshot = Assert.IsType<DashboardSnapshot>(fixture.Session.State.Snapshot);
        Assert.Equal("periodic-two", Assert.Single(snapshot.Activity.Items).Id);
        Assert.Equal("#42 Improve periodic-two", Assert.Single(snapshot.PullRequests.Items).Title);
        Assert.Equal("#42 Improve periodic-two", Assert.Single(snapshot.ReviewRequests.Items).Title);
        Assert.Equal("Review requested", Assert.Single(snapshot.ReviewRequests.Items).Detail);
        Assert.Equal("octocat/first", Assert.Single(snapshot.Repositories.Items).Title);
        Assert.Equal(5, fixture.Api.Requests.Length - before);
    }

    [Fact]
    public async Task ManualDuringSkippingPeriodicCycleCoalescesOneForcedFullFollowUp()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var automatic = RefreshResponses.Success(revision: "automatic");
        var automaticFinal = fixture.Gate();
        automatic.FinalUser = automatic.FinalUser with { Gate = automaticFinal };
        var forced = RefreshResponses.Success(revision: "forced");
        var forcedInitial = fixture.Gate();
        forced.InitialUser = forced.InitialUser with { Gate = forcedInitial };
        fixture.Api.Use(automatic, forced);
        var before = fixture.Api.Requests.Length;

        var refresh = fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic);
        await automaticFinal.EnteredAsync();
        Assert.Equal(5, fixture.Api.Requests.Length - before);
        var manual = fixture.Session.RefreshAsync(DashboardRefreshReason.Manual);
        Assert.Same(refresh, manual);
        Assert.All(Enumerable.Range(0, 20), _ =>
            Assert.Same(refresh, fixture.Session.RefreshAsync(DashboardRefreshReason.Manual)));

        using var callerCancellation = new CancellationTokenSource();
        var canceledWait = manual.WaitAsync(callerCancellation.Token);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        Assert.False(refresh.IsCompleted);

        automaticFinal.Release();
        await forcedInitial.EnteredAsync();
        Assert.Equal(6, fixture.Api.Requests.Length - before);
        Assert.All(Enumerable.Range(0, 20), _ =>
            Assert.Same(refresh, fixture.Session.RefreshAsync(DashboardRefreshReason.Manual)));
        forcedInitial.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(fixture.Session.State, revision: "forced");
        Assert.Equal(13, fixture.Api.Requests.Length - before);
        Assert.Equal(2, fixture.Api.Requests.Skip(before).Count(request => request.Route == ApiRoute.InitialUser));
        Assert.Equal(2, fixture.Api.Requests.Skip(before).Count(request => request.Route == ApiRoute.FinalUser));
    }

    [Fact]
    public async Task ManualDuringFullAutomaticCycleDoesNotQueueAnUnneededFollowUp()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        var responses = RefreshResponses.Success(revision: "full-periodic");
        var finalIdentity = fixture.Gate();
        responses.FinalUser = responses.FinalUser with { Gate = finalIdentity };
        fixture.Api.Use(responses);
        var before = fixture.Api.Requests.Length;

        var refresh = fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic);
        await finalIdentity.EnteredAsync();
        Assert.Equal(8, fixture.Api.Requests.Length - before);
        Assert.Same(refresh, fixture.Session.RefreshAsync(DashboardRefreshReason.Manual));
        finalIdentity.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(fixture.Session.State, revision: "full-periodic");
        Assert.Equal(8, fixture.Api.Requests.Length - before);
    }

    [Fact]
    public async Task ManualDuringPartialAutomaticFailureRunsOneForcedFullFollowUp()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        var automatic = RefreshResponses.Success(revision: "automatic");
        automatic.Repositories = automatic.Repositories with
        {
            Failure = new GitHubException("Repositories unavailable")
        };
        var automaticFinal = fixture.Gate();
        automatic.FinalUser = automatic.FinalUser with { Gate = automaticFinal };
        var forced = RefreshResponses.Success(revision: "forced");
        var forcedInitial = fixture.Gate();
        forced.InitialUser = forced.InitialUser with { Gate = forcedInitial };
        fixture.Api.Use(automatic, forced);
        var before = fixture.Api.Requests.Length;

        var refresh = fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic);
        await automaticFinal.EnteredAsync();
        Assert.Equal(8, fixture.Api.Requests.Length - before);
        Assert.Same(refresh, fixture.Session.RefreshAsync(DashboardRefreshReason.Manual));
        Assert.Same(refresh, fixture.Session.RefreshAsync(DashboardRefreshReason.Manual));

        automaticFinal.Release();
        await forcedInitial.EnteredAsync();
        Assert.Equal(9, fixture.Api.Requests.Length - before);
        forcedInitial.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(fixture.Session.State, revision: "forced");
        Assert.Equal(16, fixture.Api.Requests.Length - before);
        Assert.Equal(2, fixture.Api.Requests.Skip(before).Count(
            request => request.Route == ApiRoute.Repositories));
    }

    [Fact]
    public async Task ManualDuringAutomaticFinalIdentityFailureRunsOneForcedFullFollowUp()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var automatic = RefreshResponses.Success(revision: "automatic");
        var failedFinalIdentity = fixture.Gate();
        automatic.FinalUser = automatic.FinalUser with
        {
            Gate = failedFinalIdentity,
            Failure = new GitHubException("Final identity unavailable")
        };
        var forced = RefreshResponses.Success(revision: "forced");
        var forcedInitialIdentity = fixture.Gate();
        forced.InitialUser = forced.InitialUser with { Gate = forcedInitialIdentity };
        fixture.Api.Use(automatic, forced);
        var before = fixture.Api.Requests.Length;

        var refresh = fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic);
        await failedFinalIdentity.EnteredAsync();
        Assert.Equal(5, fixture.Api.Requests.Length - before);
        Assert.Same(refresh, fixture.Session.RefreshAsync(DashboardRefreshReason.Manual));
        Assert.Same(refresh, fixture.Session.RefreshAsync(DashboardRefreshReason.Manual));

        failedFinalIdentity.Release();
        await forcedInitialIdentity.EnteredAsync();
        Assert.True(fixture.Session.State.IsRefreshing);
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.Equal(6, fixture.Api.Requests.Length - before);
        forcedInitialIdentity.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(fixture.Session.State, revision: "forced");
        Assert.Equal(13, fixture.Api.Requests.Length - before);
        Assert.Equal(2, fixture.Api.Requests.Skip(before).Count(request => request.Route == ApiRoute.InitialUser));
        Assert.Equal(2, fixture.Api.Requests.Skip(before).Count(request => request.Route == ApiRoute.FinalUser));
    }

    [Fact]
    public async Task ShutdownCancelsACoalescedForcedFollowUpAndRejectsLatePublication()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var published = fixture.Session.State;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var automatic = RefreshResponses.Success(revision: "automatic");
        var automaticFinal = fixture.Gate();
        automatic.FinalUser = automatic.FinalUser with { Gate = automaticFinal };
        var forced = RefreshResponses.Success(revision: "must-not-publish");
        var forcedRepository = fixture.Gate(ignoreCancellation: true);
        forced.Repositories = forced.Repositories with { Gate = forcedRepository };
        fixture.Api.Use(automatic, forced);

        var refresh = fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic);
        await automaticFinal.EnteredAsync();
        Assert.Same(refresh, fixture.Session.RefreshAsync(DashboardRefreshReason.Manual));
        automaticFinal.Release();
        await forcedRepository.EnteredAsync();

        var shutdown = fixture.Session.ShutdownAsync();
        await forcedRepository.CanceledAsync();
        Assert.True(fixture.Session.State.IsStopping);
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.False(shutdown.IsCompleted);

        forcedRepository.Release();
        await Task.WhenAll(refresh, shutdown).WaitAsync(RefreshSessionFixture.Timeout);

        Assert.True(fixture.Session.State.IsStopping);
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.True(fixture.Session.RefreshAsync(DashboardRefreshReason.Manual).IsCompletedSuccessfully);
        SessionAssertions.Success(published);
    }

    [Fact]
    public async Task AllFreshStartupStillRejectsFinalIdentityMismatch()
    {
        var clock = new MutableTimeProvider();
        DashboardSnapshot seed;
        await using (var source = new RefreshSessionFixture(clock: clock))
        {
            await source.RefreshAsync();
            seed = SessionAssertions.Success(source.Session.State);
        }
        clock.Advance(TimeSpan.FromMinutes(1));

        await using var fixture = new RefreshSessionFixture(
            seed,
            TimeSpan.FromMinutes(5),
            clock);
        var responses = RefreshResponses.Success();
        responses.FinalUser = RefreshResponses.User("different-account");
        fixture.Api.Use(responses);

        await fixture.RefreshAsync(DashboardRefreshReason.Startup);

        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.Equal("different-account", fixture.Session.State.Account!.Login);
        Assert.Contains("account changed during refresh", fixture.Session.State.Error);
        Assert.Equal(
            [ApiRoute.InitialUser, ApiRoute.FinalUser],
            fixture.Api.Requests.Select(request => request.Route));
    }
}
