using GitHubTray.Core;

namespace GitHubTray.AppState.Tests;

public sealed class DashboardRefreshSessionIdentityTests
{
    [Fact]
    public async Task InitialIdentityFailureIsVisibleWithoutRequestingPrivateSectionsOrRetrying()
    {
        await using var fixture = new RefreshSessionFixture();
        var responses = RefreshResponses.Success();
        responses.InitialUser = responses.InitialUser with { Failure = new GitHubException("Signed out") };
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        SessionAssertions.Unverified(fixture.Session.State, null);
        Assert.Equal("Signed out", fixture.Session.State.Error);
        Assert.Equal(ApiRoute.InitialUser, Assert.Single(fixture.Api.Requests).Route);
    }

    [Fact]
    public async Task IdentityOutageKeepsRowsAndCalendarReadableUntilVerifiedRecovery()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var verified = fixture.Session.State;
        var outage = RefreshResponses.Success(revision: "unverified");
        outage.InitialUser = outage.InitialUser with { Failure = new GitHubException("Signed out") };
        fixture.Api.Use(outage);

        await fixture.RefreshAsync();
        var failed = fixture.Session.State;
        SessionAssertions.Unverified(failed, "octocat");
        Assert.Same(verified.Snapshot, failed.Snapshot);
        Assert.Equal("Signed out", failed.Error);
        Assert.Equal(9, fixture.Api.Requests.Length);
        SessionAssertions.Success(verified);

        var recovery = RefreshResponses.Success(revision: "recovered");
        var gate = fixture.Gate();
        recovery.FinalUser = recovery.FinalUser with { Gate = gate };
        fixture.Api.Use(recovery);
        var refresh = fixture.Session.RefreshAsync();
        Assert.True(fixture.Session.State.IsRefreshing);
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.NotNull(fixture.Session.State.Snapshot);
        await gate.EnteredAsync();
        Assert.NotNull(fixture.Session.State.Snapshot);
        Assert.Equal("octocat", fixture.Session.State.LastKnownLogin);
        gate.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(fixture.Session.State, revision: "recovered");
        SessionAssertions.Unverified(failed, "octocat");
        Assert.Equal(17, fixture.Api.Requests.Length);
    }

    [Theory]
    [InlineData("octocat")]
    [InlineData("OCTOCAT")]
    public async Task SameAccountRecoveryAfterIdentityOutageRetainsPrivateSectionsAndCalendarAsStale(string login)
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var verified = fixture.Session.State;
        var previous = SessionAssertions.Success(verified);
        var outage = RefreshResponses.Success();
        outage.InitialUser = outage.InitialUser with { Failure = new GitHubException("Identity unavailable") };
        fixture.Api.Use(outage);
        await fixture.RefreshAsync();
        var failed = fixture.Session.State;
        SessionAssertions.Unverified(failed, "octocat");
        Assert.NotNull(failed.Snapshot);

        var recovery = RefreshResponses.Success(login, "not-loaded");
        recovery.FailSections();
        fixture.Api.Use(recovery);
        await fixture.RefreshAsync();

        var state = fixture.Session.State;
        Assert.False(state.IsRefreshing);
        Assert.False(state.IsStopping);
        Assert.True(state.IsAccountVerified);
        Assert.Null(state.Error);
        Assert.Equal(login, state.LastKnownLogin);
        SessionAssertions.Retained(previous, Assert.IsType<DashboardSnapshot>(state.Snapshot));
        SessionAssertions.Success(verified);
        SessionAssertions.Unverified(failed, "octocat");
        Assert.Equal(17, fixture.Api.Requests.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountSwitchNeverInheritsPreviousPrivateDataEvenAfterAnIdentityOutage(bool identityOutage)
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var original = fixture.Session.State;
        if (identityOutage)
        {
            var outage = RefreshResponses.Success();
            outage.InitialUser = outage.InitialUser with { Failure = new GitHubException("Signed out") };
            fixture.Api.Use(outage);
            await fixture.RefreshAsync();
            SessionAssertions.Unverified(fixture.Session.State, "octocat");
            Assert.NotNull(fixture.Session.State.Snapshot);
        }
        var switched = RefreshResponses.Success("different-account", "new-private");
        switched.FailSections();
        fixture.Api.Use(switched);

        await fixture.RefreshAsync();

        var state = fixture.Session.State;
        Assert.True(state.IsAccountVerified);
        Assert.False(state.IsRefreshing);
        Assert.Null(state.Error);
        Assert.Equal("different-account", state.LastKnownLogin);
        var snapshot = Assert.IsType<DashboardSnapshot>(state.Snapshot);
        Assert.Equal("different-account", snapshot.User.Login);
        foreach (var section in SessionAssertions.Sections(snapshot))
        {
            Assert.Empty(section.Items);
            Assert.Null(section.UpdatedAt);
            Assert.Equal("Offline", section.Error);
            Assert.False(section.IsStale);
        }
        Assert.Null(snapshot.Contributions.Calendar);
        Assert.Null(snapshot.Contributions.UpdatedAt);
        Assert.Equal("Offline", snapshot.Contributions.Error);
        Assert.False(snapshot.Contributions.IsStale);
        Assert.Null(snapshot.Copilot.Usage);
        Assert.Null(snapshot.Copilot.UpdatedAt);
        Assert.Equal("Offline", snapshot.Copilot.Error);
        Assert.Equal(identityOutage ? 17 : 16, fixture.Api.Requests.Length);
        SessionAssertions.Success(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphQlViewerMismatchFailsClosedInsteadOfPublishingMixedAccountData(bool hasPrevious)
    {
        await using var fixture = new RefreshSessionFixture();
        DashboardSnapshot? previous = null;
        if (hasPrevious)
        {
            await fixture.RefreshAsync();
            previous = SessionAssertions.Success(fixture.Session.State);
        }
        var responses = RefreshResponses.Success(revision: "must-not-publish");
        responses.Contributions = RefreshResponses.Calendar("different-account");
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        SessionAssertions.Unverified(fixture.Session.State, "octocat");
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.Equal("The GitHub account changed while loading contributions. Refresh again to load the current account.",
            fixture.Session.State.Error);
        Assert.Equal(hasPrevious ? 15 : 7, fixture.Api.Requests.Length);
        Assert.Equal(hasPrevious ? 1 : 0, fixture.Api.Requests.Count(request => request.Route == ApiRoute.FinalUser));

        if (previous is not null)
        {
            var recovery = RefreshResponses.Success();
            recovery.FailSections();
            fixture.Api.Use(recovery);
            await fixture.RefreshAsync();
            Assert.Null(fixture.Session.State.Error);
            var recovered = Assert.IsType<DashboardSnapshot>(fixture.Session.State.Snapshot);
            Assert.All(SessionAssertions.Sections(recovered), section => Assert.Empty(section.Items));
            Assert.Null(recovered.Contributions.Calendar);
            Assert.Null(recovered.Copilot.Usage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFinalIdentityCheckRejectsNewSectionsAndPreservesOnlyVerifiedRecovery(bool accountChanged)
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var originalState = fixture.Session.State;
        var original = SessionAssertions.Success(originalState);
        var gate = fixture.Gate();
        var responses = RefreshResponses.Success(revision: "must-not-publish");
        responses.Contributions = RefreshResponses.Calendar("octocat", allZero: true);
        responses.FinalUser = accountChanged
            ? RefreshResponses.User("different-account") with { Gate = gate }
            : responses.FinalUser with { Gate = gate, Failure = new GitHubException("Final identity unavailable") };
        fixture.Api.Use(responses);
        var refresh = fixture.Session.RefreshAsync();
        await gate.EnteredAsync();
        Assert.Equal("first", Assert.Single(fixture.Session.State.Snapshot!.Activity.Items).Id);
        Assert.True(fixture.Session.State.IsRefreshing);
        gate.Release();

        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        if (accountChanged)
        {
            Assert.True(fixture.Session.State.IsAccountVerified);
            Assert.Null(fixture.Session.State.Snapshot);
            Assert.Equal("different-account", fixture.Session.State.Account!.Login);
        }
        else
        {
            SessionAssertions.Unverified(fixture.Session.State, "octocat");
            Assert.Same(original, fixture.Session.State.Snapshot);
        }
        Assert.Equal(accountChanged
                ? "The GitHub account changed during refresh. No new data was displayed. Refresh again to load the current account."
                : "Final identity unavailable",
            fixture.Session.State.Error);
        Assert.Equal(16, fixture.Api.Requests.Length);
        Assert.Equal(ApiRoute.FinalUser, fixture.Api.Requests[^1].Route);

        var recovery = RefreshResponses.Success();
        recovery.FailSections();
        fixture.Api.Use(recovery);
        await fixture.RefreshAsync();
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.Null(fixture.Session.State.Error);
        var recovered = Assert.IsType<DashboardSnapshot>(fixture.Session.State.Snapshot);
        if (accountChanged)
        {
            Assert.All(SessionAssertions.Sections(recovered), section => Assert.Empty(section.Items));
            Assert.Null(recovered.Contributions.Calendar);
            Assert.Null(recovered.Copilot.Usage);
        }
        else
        {
            SessionAssertions.Retained(original, recovered);
        }
        SessionAssertions.Success(originalState);
        Assert.Equal(24, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task FailedFinalIdentityWithoutCacheLeavesConfirmedInitialAccountUnverified()
    {
        await using var fixture = new RefreshSessionFixture();
        var responses = RefreshResponses.Success(revision: "must-not-publish");
        responses.FinalUser = responses.FinalUser with
        {
            Failure = new GitHubException("Final identity unavailable")
        };
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        SessionAssertions.Unverified(fixture.Session.State, "octocat");
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.Equal("octocat", fixture.Session.State.Account!.Login);
        Assert.Equal("Final identity unavailable", fixture.Session.State.Error);
    }

    [Fact]
    public async Task InitialIdentityFailureAfterConfirmedSwitchDoesNotRetainVerifiedTrust()
    {
        await using var fixture = new RefreshSessionFixture();
        var switched = RefreshResponses.Success(revision: "must-not-publish");
        switched.FinalUser = RefreshResponses.User("different-account");
        fixture.Api.Use(switched);
        await fixture.RefreshAsync();
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.Equal("different-account", fixture.Session.State.Account!.Login);

        var outage = RefreshResponses.Success("different-account");
        outage.InitialUser = outage.InitialUser with
        {
            Failure = new GitHubException("Initial identity unavailable")
        };
        fixture.Api.Use(outage);
        await fixture.RefreshAsync();

        SessionAssertions.Unverified(fixture.Session.State, "different-account");
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.Equal("different-account", fixture.Session.State.Account!.Login);
        Assert.Equal("Initial identity unavailable", fixture.Session.State.Error);
    }

    [Fact]
    public async Task CaseOnlyViewerAndFinalIdentityDifferencesRemainVerified()
    {
        await using var fixture = new RefreshSessionFixture();
        var responses = RefreshResponses.Success();
        responses.Contributions = RefreshResponses.Calendar("OCTOCAT");
        responses.FinalUser = RefreshResponses.User("OCTOCAT");
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        SessionAssertions.Success(fixture.Session.State);
        Assert.Equal(8, fixture.Api.Requests.Length);
    }
}
