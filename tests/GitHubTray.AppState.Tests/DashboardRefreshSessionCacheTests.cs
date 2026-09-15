using GitHubTray.Core;

namespace GitHubTray.AppState.Tests;

public sealed class DashboardRefreshSessionCacheTests
{
    [Fact]
    public async Task AllFreshHydratedStartupUsesOnlyIdentityAndPersistsOriginalSuccessTimes()
    {
        var cache = new MemoryDashboardCacheStore();
        var seed = await SeedAsync(cache);
        var writesBeforeStartup = cache.WriteCount;
        await using var fixture = new RefreshSessionFixture(cache);
        var responses = RefreshResponses.Success(revision: "must-not-load");
        var finalIdentity = fixture.Gate();
        responses.FinalUser = responses.FinalUser with { Gate = finalIdentity };
        fixture.Api.Use(responses);

        var refresh = fixture.Session.RefreshAsync(DashboardRefreshReason.Startup);
        await fixture.Session.InitialHydrationTask.WaitAsync(RefreshSessionFixture.Timeout);
        await finalIdentity.EnteredAsync();

        var hydrated = fixture.Session.State;
        Assert.True(hydrated.IsRefreshing);
        var snapshot = Assert.IsType<DashboardSnapshot>(hydrated.Snapshot);
        Assert.Equal(
            [ApiRoute.InitialUser, ApiRoute.FinalUser],
            fixture.Api.Requests.Select(request => request.Route));
        Assert.All(SessionAssertions.Sections(snapshot),
            section => Assert.Equal(DashboardSectionSource.Cached, section.Source));
        Assert.Equal(DashboardSectionSource.Cached, snapshot.Contributions.Source);
        Assert.Equal(DashboardSectionSource.Cached, snapshot.Copilot.Source);
        Assert.Equal(seed.Activity.UpdatedAt, snapshot.Activity.UpdatedAt);
        Assert.Equal(seed.PullRequests.UpdatedAt, snapshot.PullRequests.UpdatedAt);
        Assert.Equal(seed.ReviewRequests.UpdatedAt, snapshot.ReviewRequests.UpdatedAt);
        Assert.Equal(seed.Repositories.UpdatedAt, snapshot.Repositories.UpdatedAt);
        Assert.Equal(seed.Contributions.UpdatedAt, snapshot.Contributions.UpdatedAt);
        Assert.Equal(seed.Copilot.UpdatedAt, snapshot.Copilot.UpdatedAt);
        Assert.Equal(writesBeforeStartup, cache.WriteCount);

        finalIdentity.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(fixture.Session.State, revision: "seed");
        Assert.Equal(writesBeforeStartup + 1, cache.WriteCount);
        var persisted = Assert.IsType<DashboardCacheRecord>(cache.LastWrite);
        Assert.Equal(seed.Activity.UpdatedAt, persisted.Activity!.SucceededAt);
        Assert.Equal(seed.PullRequests.UpdatedAt, persisted.PullRequests!.SucceededAt);
        Assert.Equal(seed.ReviewRequests.UpdatedAt, persisted.ReviewRequests!.SucceededAt);
        Assert.Equal(seed.Repositories.UpdatedAt, persisted.Repositories!.SucceededAt);
        Assert.Equal(seed.Contributions.UpdatedAt, persisted.Contributions!.SucceededAt);
        Assert.Equal(seed.Copilot.UpdatedAt, persisted.Copilot!.SucceededAt);
    }

    [Fact]
    public async Task MatchingCachePublishesAfterInitialIdentityBeforeDelayedLiveSections()
    {
        var cache = new MemoryDashboardCacheStore();
        await SeedAsync(cache);
        await using var fixture = new RefreshSessionFixture(cache);
        var activityGate = fixture.Gate();
        var responses = RefreshResponses.Success(revision: "live");
        responses.Activity = responses.Activity with { Gate = activityGate };
        fixture.Api.Use(responses);

        var refresh = fixture.Session.RefreshAsync();
        await fixture.Session.InitialHydrationTask.WaitAsync(RefreshSessionFixture.Timeout);
        await activityGate.EnteredAsync();

        var hydrated = fixture.Session.State;
        Assert.True(hydrated.IsRefreshing);
        Assert.True(hydrated.IsAccountVerified);
        var snapshot = Assert.IsType<DashboardSnapshot>(hydrated.Snapshot);
        Assert.Equal("seed", Assert.Single(snapshot.Activity.Items).Id);
        Assert.All(SessionAssertions.Sections(snapshot),
            section => Assert.Equal(DashboardSectionSource.Cached, section.Source));
        Assert.Equal(DashboardSectionSource.Cached, snapshot.Contributions.Source);
        Assert.Equal(DashboardSectionSource.Cached, snapshot.Copilot.Source);
        Assert.Equal(1, fixture.Api.Requests.Count(request => request.Route == ApiRoute.InitialUser));
        Assert.DoesNotContain(fixture.Api.Requests, request => request.Route == ApiRoute.FinalUser);

        activityGate.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(fixture.Session.State, revision: "live");
        Assert.All(SessionAssertions.Sections(fixture.Session.State.Snapshot!),
            section => Assert.Equal(DashboardSectionSource.Live, section.Source));
        Assert.Equal(8, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task InitialIdentityFailureNeverReadsOrPublishesExistingCache()
    {
        var cache = new MemoryDashboardCacheStore();
        await SeedAsync(cache);
        var readsBeforeFailure = cache.ReadCount;
        await using var fixture = new RefreshSessionFixture(cache);
        var responses = RefreshResponses.Success();
        responses.InitialUser = responses.InitialUser with
        {
            Failure = new GitHubException("Identity unavailable")
        };
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        SessionAssertions.Unverified(fixture.Session.State, null);
        Assert.Equal(readsBeforeFailure, cache.ReadCount);
        Assert.Equal(ApiRoute.InitialUser, Assert.Single(fixture.Api.Requests).Route);
    }

    [Fact]
    public async Task AccountSwitchCannotHydrateOrOverwriteAnotherAccountsRecord()
    {
        var cache = new MemoryDashboardCacheStore();
        await SeedAsync(cache);
        await using var fixture = new RefreshSessionFixture(cache);
        var finalGate = fixture.Gate();
        var responses = RefreshResponses.Success("different-account", "live-other");
        responses.FinalUser = responses.FinalUser with { Gate = finalGate };
        fixture.Api.Use(responses);

        var refresh = fixture.Session.RefreshAsync();
        await fixture.Session.InitialHydrationTask.WaitAsync(RefreshSessionFixture.Timeout);
        await finalGate.EnteredAsync();

        Assert.False(fixture.Session.State.IsAccountVerified);
        Assert.Null(fixture.Session.State.Snapshot);

        finalGate.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);
        SessionAssertions.Success(fixture.Session.State, "different-account", "live-other");

        await using var original = new RefreshSessionFixture(cache);
        var originalResponses = RefreshResponses.Success(revision: "must-fail");
        originalResponses.FailSections();
        original.Api.Use(originalResponses);
        await original.RefreshAsync();
        var retained = Assert.IsType<DashboardSnapshot>(original.Session.State.Snapshot);
        Assert.Equal("seed", Assert.Single(retained.Activity.Items).Id);
    }

    [Fact]
    public async Task FailedFinalIdentityHidesHydratedCacheAndDoesNotPersistNewSections()
    {
        var cache = new MemoryDashboardCacheStore();
        await SeedAsync(cache);
        var writesBeforeFailure = cache.WriteCount;
        await using var fixture = new RefreshSessionFixture(cache);
        var finalGate = fixture.Gate();
        var responses = RefreshResponses.Success(revision: "must-not-persist");
        responses.FinalUser = RefreshResponses.User("different-account") with { Gate = finalGate };
        fixture.Api.Use(responses);

        var refresh = fixture.Session.RefreshAsync();
        await fixture.Session.InitialHydrationTask.WaitAsync(RefreshSessionFixture.Timeout);
        await finalGate.EnteredAsync();

        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.Equal("seed", Assert.Single(fixture.Session.State.Snapshot!.Activity.Items).Id);

        finalGate.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Unverified(fixture.Session.State, "octocat");
        Assert.Equal(writesBeforeFailure, cache.WriteCount);
        Assert.Equal(8, fixture.Api.Requests.Length);
    }

    private static async Task<DashboardSnapshot> SeedAsync(MemoryDashboardCacheStore cache)
    {
        await using var seed = new RefreshSessionFixture(cache);
        seed.Api.Use(RefreshResponses.Success(revision: "seed"));
        await seed.RefreshAsync();
        return SessionAssertions.Success(seed.Session.State, revision: "seed");
    }
}
