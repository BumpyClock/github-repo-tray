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
    public async Task IdentityOutageHidesRowsAndCalendarUntilAnExplicitVerifiedRecovery()
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
        Assert.Equal("Signed out", failed.Error);
        Assert.Equal(8, fixture.Api.Requests.Length);
        SessionAssertions.Success(verified);

        var recovery = RefreshResponses.Success(revision: "recovered");
        var gate = fixture.Gate();
        recovery.FinalUser = recovery.FinalUser with { Gate = gate };
        fixture.Api.Use(recovery);
        var refresh = fixture.Session.RefreshAsync();
        Assert.True(fixture.Session.State.IsRefreshing);
        Assert.False(fixture.Session.State.IsAccountVerified);
        Assert.Null(fixture.Session.State.Snapshot);
        await gate.EnteredAsync();
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.Equal("octocat", fixture.Session.State.LastKnownLogin);
        gate.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(fixture.Session.State, revision: "recovered");
        SessionAssertions.Unverified(failed, "octocat");
        Assert.Equal(15, fixture.Api.Requests.Length);
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
        Assert.Equal(15, fixture.Api.Requests.Length);
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
        Assert.Equal(identityOutage ? 15 : 14, fixture.Api.Requests.Length);
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

        SessionAssertions.Unverified(fixture.Session.State, hasPrevious ? "octocat" : null);
        Assert.Equal("The GitHub account changed while loading contributions. Refresh again to load the current account.",
            fixture.Session.State.Error);
        Assert.Equal(hasPrevious ? 13 : 6, fixture.Api.Requests.Length);
        Assert.Equal(hasPrevious ? 1 : 0, fixture.Api.Requests.Count(request => request.Route == ApiRoute.FinalUser));

        if (previous is not null)
        {
            var recovery = RefreshResponses.Success();
            recovery.FailSections();
            fixture.Api.Use(recovery);
            await fixture.RefreshAsync();
            Assert.Null(fixture.Session.State.Error);
            SessionAssertions.Retained(previous, Assert.IsType<DashboardSnapshot>(fixture.Session.State.Snapshot));
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

        SessionAssertions.Unverified(fixture.Session.State, "octocat");
        Assert.Equal(accountChanged
                ? "The GitHub account changed during refresh. No new data was displayed. Refresh again to load the current account."
                : "Final identity unavailable",
            fixture.Session.State.Error);
        Assert.Equal(14, fixture.Api.Requests.Length);
        Assert.Equal(ApiRoute.FinalUser, fixture.Api.Requests[^1].Route);

        var recovery = RefreshResponses.Success();
        recovery.FailSections();
        fixture.Api.Use(recovery);
        await fixture.RefreshAsync();
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.Null(fixture.Session.State.Error);
        SessionAssertions.Retained(original, Assert.IsType<DashboardSnapshot>(fixture.Session.State.Snapshot));
        SessionAssertions.Success(originalState);
        Assert.Equal(21, fixture.Api.Requests.Length);
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
        Assert.Equal(7, fixture.Api.Requests.Length);
    }
}
