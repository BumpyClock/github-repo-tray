using System.Text.Json;
using GitHubTray.Core;

namespace GitHubTray.Core.Tests;

public sealed class DashboardServiceTests
{
    [Fact]
    public async Task RefreshLoadsAllFourSectionsForTheAuthenticatedUser()
    {
        var api = new FakeApi();
        var snapshot = await new DashboardService(api).RefreshAsync();

        Assert.Equal("octocat", snapshot.User.Login);
        Assert.Equal("The Octocat", snapshot.User.DisplayName);
        Assert.Equal("Pushed commits", Assert.Single(snapshot.Activity.Items).Title);
        Assert.Equal("main", snapshot.Activity.Items[0].Detail);
        Assert.Equal("#42 Improve tray", Assert.Single(snapshot.PullRequests.Items).Title);
        Assert.Equal("Review requested", Assert.Single(snapshot.ReviewRequests.Items).Detail);
        Assert.Equal("octocat/tray", Assert.Single(snapshot.Repositories.Items).Title);
        Assert.Contains(api.Queries, query => query.Contains("author:octocat"));
        Assert.Contains(api.Queries, query => query.Contains("review-requested:octocat"));
        Assert.NotNull(snapshot.Copilot.Usage);
        Assert.All(api.Endpoints.Where(endpoint => endpoint != "user" && endpoint != CopilotUsageParser.Endpoint),
            endpoint => Assert.Contains("per_page=30", endpoint));
    }

    [Fact]
    public async Task FailedSectionRetainsLastSuccessWithoutBlockingOtherSections()
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        api.FailActivity = true;

        var refreshed = await service.RefreshAsync(previous);

        Assert.Same(previous.Activity.Items, refreshed.Activity.Items);
        Assert.Equal(previous.Activity.UpdatedAt, refreshed.Activity.UpdatedAt);
        Assert.True(refreshed.Activity.IsStale);
        Assert.Equal("Offline", refreshed.Activity.Error);
        Assert.Null(refreshed.PullRequests.Error);
        Assert.False(refreshed.PullRequests.IsStale);
        Assert.True(refreshed.PullRequests.UpdatedAt >= previous.PullRequests.UpdatedAt);
    }

    [Fact]
    public async Task AccountSwitchNeverRetainsPreviousAccountsPrivateData()
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        api.Login = "different-user";
        api.FailActivity = true;

        var refreshed = await service.RefreshAsync(previous);

        Assert.Equal("different-user", refreshed.User.Login);
        Assert.Empty(refreshed.Activity.Items);
        Assert.Null(refreshed.Activity.UpdatedAt);
        Assert.False(refreshed.Activity.IsStale);
        Assert.NotNull(refreshed.Activity.Error);
    }

    [Fact]
    public async Task FailedInitialLoadIsUnavailableRatherThanAnEmptySuccess()
    {
        var api = new FakeApi { FailActivity = true };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Empty(snapshot.Activity.Items);
        Assert.Null(snapshot.Activity.UpdatedAt);
        Assert.NotNull(snapshot.Activity.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountChangeDuringRefreshRejectsTheEntireSnapshot(bool hasPrevious)
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = hasPrevious ? await service.RefreshAsync() : null;
        api.AfterCopilot = () => api.Login = "different-account";

        var error = await Assert.ThrowsAsync<GitHubAccountChangedException>(() => service.RefreshAsync(previous));

        Assert.Contains("account changed during refresh", error.Message);
        Assert.Equal("user", api.Endpoints[^1]);
        if (previous is not null)
        {
            Assert.Equal("octocat", previous.User.Login);
        }
    }

    [Fact]
    public async Task FailedFinalAccountCheckDoesNotPublishFetchedSections()
    {
        var api = new FakeApi();
        api.AfterQuery = () => api.FailUser = true;

        await Assert.ThrowsAsync<GitHubException>(() => new DashboardService(api).RefreshAsync());

        Assert.Equal(2, api.Endpoints.Count(endpoint => endpoint == "user"));
        Assert.Equal("user", api.Endpoints[^1]);
    }

    [Fact]
    public async Task FinalAccountCheckAcceptsCaseOnlyLoginChanges()
    {
        var api = new FakeApi();
        api.AfterQuery = () => api.Login = "OCTOCAT";

        var snapshot = await new DashboardService(api).RefreshAsync();

        Assert.Equal("octocat", snapshot.User.Login);
        Assert.Single(snapshot.Repositories.Items);
        Assert.Equal(2, api.Endpoints.Count(endpoint => endpoint == "user"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("""{"id":1,"login":"invalid/user","html_url":"https://github.com/user"}""")]
    public async Task MalformedIdentityIsReportedAsBoundaryFailure(string response)
    {
        var api = new FakeApi { UserResponse = response };
        var error = await Assert.ThrowsAsync<GitHubException>(() => new DashboardService(api).RefreshAsync());
        Assert.Contains("unexpected response", error.Message);
        Assert.Single(api.Endpoints);
    }

    [Theory]
    [InlineData("file:///C:/secret")]
    [InlineData("https://github.com.evil.example/octocat/tray/pull/42")]
    [InlineData("https://user:password@github.com/octocat/tray/pull/42")]
    [InlineData("http://github.com/octocat/tray/pull/42")]
    [InlineData("https://github.com:444/octocat/tray/pull/42")]
    public async Task UnsafeLinksInvalidateTheSectionInsteadOfReachingTheShell(string url)
    {
        var api = new FakeApi { PullUrl = url };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Empty(snapshot.PullRequests.Items);
        Assert.Contains("unexpected response", snapshot.PullRequests.Error);
        Assert.Null(snapshot.Activity.Error);
    }

    [Fact]
    public async Task SuccessfulEmptyResponseClearsOldItems()
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        api.ActivityResponse = "[]";
        var snapshot = await service.RefreshAsync(previous);
        Assert.Empty(snapshot.Activity.Items);
        Assert.Null(snapshot.Activity.Error);
        Assert.NotNull(snapshot.Activity.UpdatedAt);
    }

    [Fact]
    public async Task WhitespaceRepositoryLanguageUsesRepositoryFallback()
    {
        var api = new FakeApi
        {
            RepositoryResponse =
                """[{"full_name":"octocat/tray","html_url":"https://github.com/octocat/tray","description":"A native tray app","language":" ","private":true,"archived":false,"pushed_at":null,"updated_at":"2026-09-01T10:30:00Z"}]"""
        };

        var snapshot = await new DashboardService(api).RefreshAsync();

        Assert.Equal("Repository", Assert.Single(snapshot.Repositories.Items).Repository);
    }

    [Fact]
    public async Task CacheValidationFailureDoesNotDiscardLiveSnapshot()
    {
        var diagnostics = new List<DashboardCacheDiagnostic>();
        var cache = new RecordingCacheStore
        {
            WriteException = new ArgumentException("Injected cache rejection.")
        };

        var snapshot = await new DashboardService(
            new FakeApi(), cache, reportCacheDiagnostic: diagnostics.Add).RefreshAsync();

        Assert.Equal("octocat", snapshot.User.Login);
        Assert.Single(snapshot.Repositories.Items);
        Assert.Equal(
            DashboardCacheDiagnosticKind.WriteFailed,
            Assert.Single(diagnostics).Kind);
    }

    [Fact]
    public async Task CacheValidationFailurePreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var cache = new RecordingCacheStore
        {
            BeforeWrite = _ => cancellation.Cancel(),
            WriteException = new ArgumentException("Injected cache rejection.")
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DashboardService(new FakeApi(), cache).RefreshAsync(null, cancellation.Token));
    }

    [Fact]
    public async Task AllFreshStartupSnapshotUsesOnlyTheTwoIdentityOperations()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeApi();
        var service = new DashboardService(api, timeProvider: clock);
        var previous = await service.RefreshAsync();
        api.ClearRequests();
        clock.Advance(TimeSpan.FromMinutes(4));

        var result = await service.RefreshAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Startup, TimeSpan.FromMinutes(5)),
            previous);

        Assert.Equal(DashboardSectionKind.All, result.ReusedSections);
        Assert.Equal(["user", "user"], api.Endpoints);
        Assert.Empty(api.Queries);
        Assert.Same(previous.Activity, result.Snapshot.Activity);
        Assert.Same(previous.PullRequests, result.Snapshot.PullRequests);
        Assert.Same(previous.ReviewRequests, result.Snapshot.ReviewRequests);
        Assert.Same(previous.Repositories, result.Snapshot.Repositories);
        Assert.Same(previous.Contributions, result.Snapshot.Contributions);
        Assert.Same(previous.Copilot, result.Snapshot.Copilot);
    }

    [Fact]
    public async Task PeriodicRefreshAlwaysFetchesActivityAndPullRequestsButReusesOtherFreshSections()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeApi();
        var service = new DashboardService(api, timeProvider: clock);
        var previous = await service.RefreshAsync();
        api.ClearRequests();
        clock.Advance(TimeSpan.FromMinutes(1));

        var result = await service.RefreshAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Periodic, TimeSpan.FromMinutes(5)),
            previous);

        Assert.Equal(
            DashboardSectionKind.Repositories | DashboardSectionKind.Contributions | DashboardSectionKind.Copilot,
            result.ReusedSections);
        Assert.Equal(5, api.OperationCount);
        Assert.Equal(2, api.Endpoints.Count(endpoint => endpoint == "user"));
        Assert.Contains(api.Endpoints, endpoint => endpoint.StartsWith("users/", StringComparison.Ordinal));
        Assert.Equal(2, api.Queries.Count(query => query.Contains("query PullRequests", StringComparison.Ordinal)));
        Assert.DoesNotContain(api.Endpoints, endpoint => endpoint.StartsWith("user/repos?", StringComparison.Ordinal));
        Assert.DoesNotContain(api.Queries, query => query.Contains("contributionsCollection", StringComparison.Ordinal));
        Assert.DoesNotContain(CopilotUsageParser.Endpoint, api.Endpoints);
        Assert.Same(previous.Repositories, result.Snapshot.Repositories);
        Assert.Same(previous.Contributions, result.Snapshot.Contributions);
        Assert.Same(previous.Copilot, result.Snapshot.Copilot);
    }

    [Fact]
    public async Task ExactFreshnessBoundariesRequireFetches()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeApi();
        var service = new DashboardService(api, timeProvider: clock);
        var previous = await service.RefreshAsync();

        api.ClearRequests();
        clock.Advance(TimeSpan.FromMinutes(5));
        var copilotBoundary = await service.RefreshAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Startup, TimeSpan.FromMinutes(10)),
            previous);
        Assert.Equal(DashboardSectionKind.All & ~DashboardSectionKind.Copilot, copilotBoundary.ReusedSections);
        Assert.Equal(3, api.OperationCount);
        Assert.Equal([CopilotUsageParser.Endpoint], api.Endpoints.Where(endpoint => endpoint != "user"));

        api.ClearRequests();
        clock.Advance(TimeSpan.FromMinutes(5));
        var configuredBoundary = await service.RefreshAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Startup, TimeSpan.FromMinutes(10)),
            previous);
        Assert.Equal(
            DashboardSectionKind.Repositories | DashboardSectionKind.Contributions,
            configuredBoundary.ReusedSections);
        Assert.Equal(6, api.OperationCount);

        api.ClearRequests();
        clock.Advance(TimeSpan.FromMinutes(5));
        var fifteenMinuteBoundary = await service.RefreshAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Startup, TimeSpan.FromMinutes(20)),
            previous);
        Assert.Equal(
            DashboardSectionKind.Activity | DashboardSectionKind.PullRequests | DashboardSectionKind.ReviewRequests,
            fifteenMinuteBoundary.ReusedSections);
        Assert.Equal(5, api.OperationCount);
        Assert.Contains(api.Endpoints, endpoint => endpoint.StartsWith("user/repos?", StringComparison.Ordinal));
        Assert.Contains(api.Queries, query => query.Contains("contributionsCollection", StringComparison.Ordinal));
        Assert.Contains(CopilotUsageParser.Endpoint, api.Endpoints);
    }

    [Fact]
    public async Task ManualRefreshBypassesFreshnessForEverySection()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeApi();
        var service = new DashboardService(api, timeProvider: clock);
        var previous = await service.RefreshAsync();
        api.ClearRequests();

        var result = await service.RefreshAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Manual, TimeSpan.Zero),
            previous);

        Assert.Equal(DashboardSectionKind.None, result.ReusedSections);
        Assert.Equal(8, api.OperationCount);
        Assert.Equal(2, api.Endpoints.Count(endpoint => endpoint == "user"));
        Assert.Equal(3, api.Queries.Count);
    }

    [Fact]
    public async Task ReusedFailedSectionPreservesItsOriginalSuccessTimestampAndFailure()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeApi();
        var service = new DashboardService(api, timeProvider: clock);
        var original = await service.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        api.FailRepositories = true;
        var failed = await service.RefreshAsync(original);
        Assert.Equal(original.Repositories.UpdatedAt, failed.Repositories.UpdatedAt);
        Assert.Equal("Offline", failed.Repositories.Error);

        api.ClearRequests();
        clock.Advance(TimeSpan.FromMinutes(1));
        var result = await service.RefreshAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Periodic, TimeSpan.FromMinutes(5)),
            failed);

        Assert.True(result.ReusedSections.HasFlag(DashboardSectionKind.Repositories));
        Assert.Same(failed.Repositories, result.Snapshot.Repositories);
        Assert.Equal(original.Repositories.UpdatedAt, result.Snapshot.Repositories.UpdatedAt);
        Assert.Equal("Offline", result.Snapshot.Repositories.Error);
        Assert.Equal(5, api.OperationCount);
        Assert.DoesNotContain(api.Endpoints, endpoint => endpoint.StartsWith("user/repos?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulEmptySectionRemainsAReusableSuccess()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeApi();
        var service = new DashboardService(api, timeProvider: clock);
        var previous = await service.RefreshAsync();
        api.RepositoryResponse = "[]";
        clock.Advance(TimeSpan.FromMinutes(1));
        var empty = await service.RefreshAsync(previous);
        Assert.Empty(empty.Repositories.Items);
        Assert.NotNull(empty.Repositories.UpdatedAt);
        Assert.Null(empty.Repositories.Error);

        api.ClearRequests();
        clock.Advance(TimeSpan.FromMinutes(1));
        var result = await service.RefreshAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Periodic, TimeSpan.FromMinutes(5)),
            empty);

        Assert.True(result.ReusedSections.HasFlag(DashboardSectionKind.Repositories));
        Assert.Empty(result.Snapshot.Repositories.Items);
        Assert.Equal(empty.Repositories.UpdatedAt, result.Snapshot.Repositories.UpdatedAt);
        Assert.DoesNotContain(api.Endpoints, endpoint => endpoint.StartsWith("user/repos?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshSnapshotFromAnotherAccountIsNeverReused()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeApi();
        var service = new DashboardService(api, timeProvider: clock);
        var previous = await service.RefreshAsync();
        api.ClearRequests();
        api.Login = "different-user";
        clock.Advance(TimeSpan.FromMinutes(1));

        var result = await service.RefreshAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Startup, TimeSpan.FromMinutes(5)),
            previous);

        Assert.Equal(DashboardSectionKind.None, result.ReusedSections);
        Assert.Equal(8, api.OperationCount);
        Assert.Equal("different-user", result.Snapshot.User.Login);
        Assert.NotSame(previous.Activity, result.Snapshot.Activity);
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("identity")]
    public async Task RetentionEligibilityUsesTimeAfterSlowStartupWork(string slowPhase)
    {
        var start = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(start);
        var cachedAt = start - JsonDashboardCacheStore.Retention + TimeSpan.FromMinutes(1);
        var cache = new RecordingCacheStore
        {
            ReadRecord = new(
                DashboardCacheVersions.Schema,
                new("github.com", 1, "octocat"),
                new(
                    DashboardCacheVersions.Activity,
                    cachedAt,
                    [
                        new(
                            "cached",
                            "Cached",
                            "octocat/tray",
                            "cached",
                            cachedAt,
                            new Uri("https://github.com/octocat/tray"))
                    ]),
                null, null, null, null, null)
        };
        var api = new FakeApi();
        if (slowPhase == "cache")
        {
            cache.BeforeRead = () => clock.Advance(TimeSpan.FromMinutes(2));
        }
        else
        {
            api.BeforeUser = () =>
            {
                api.BeforeUser = null;
                clock.Advance(TimeSpan.FromMinutes(2));
            };
        }

        var hydratedSnapshots = new List<DashboardSnapshot?>();
        var result = await new DashboardService(api, cache, clock)
            .RefreshWithHydrationAsync(
                new(
                    DashboardRefreshReason.Startup,
                    JsonDashboardCacheStore.Retention + TimeSpan.FromDays(1)),
                null,
                hydratedSnapshots.Add);

        if (slowPhase == "cache")
        {
            Assert.Null(Assert.Single(hydratedSnapshots));
        }
        else
        {
            Assert.Equal("cached", Assert.Single(
                Assert.IsType<DashboardSnapshot>(Assert.Single(hydratedSnapshots)).Activity.Items).Id);
        }
        Assert.Contains(api.Endpoints,
            endpoint => endpoint.StartsWith("users/", StringComparison.Ordinal));
        Assert.Equal("1", Assert.Single(result.Snapshot.Activity.Items).Id);
        Assert.Equal(DashboardSectionSource.Live, result.Snapshot.Activity.Source);
    }

    [Fact]
    public async Task UnknownEventTypesStillHaveSafeRepositoryLinksAndTimestamps()
    {
        var api = new FakeApi
        {
            ActivityResponse = """[{"id":"new","type":"FutureEvent","repo":{"name":"org/repo"},"payload":{},"created_at":"2026-09-01T09:30:00Z"}]"""
        };
        var snapshot = await new DashboardService(api).RefreshAsync();
        var item = Assert.Single(snapshot.Activity.Items);
        Assert.Equal("Repository activity", item.Title);
        Assert.Equal("https://github.com/org/repo", item.Url.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 9, 30, 0, TimeSpan.Zero), item.UpdatedAt);
    }

    [Fact]
    public async Task ActivityIsBoundedEvenIfTheServerReturnsMoreItems()
    {
        var api = new FakeApi
        {
            ActivityResponse = JsonSerializer.Serialize(Enumerable.Range(0, 40).Select(index => new
            {
                id = index.ToString(),
                type = "PublicEvent",
                repo = new { name = "org/repo" },
                payload = new { },
                created_at = "2026-09-01T09:30:00Z"
            }))
        };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Equal(30, snapshot.Activity.Items.Count);
    }

    [Fact]
    public async Task ActivityIsOrderedByEventTimeRatherThanIngestionOrder()
    {
        var api = new FakeApi
        {
            ActivityResponse = """
                [
                    {"id":"older","type":"PublicEvent","repo":{"name":"org/repo"},"payload":{},"created_at":"2026-09-01T09:30:00Z"},
                    {"id":"newer","type":"PublicEvent","repo":{"name":"org/repo"},"payload":{},"created_at":"2026-09-01T10:30:00Z"}
                ]
                """
        };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Equal(["newer", "older"], snapshot.Activity.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task NewestEventsBeyondTheInputLimitAreRetained()
    {
        var api = new FakeApi
        {
            ActivityResponse = JsonSerializer.Serialize(Enumerable.Range(0, 40).Select(index => new
            {
                id = index.ToString(),
                type = "PublicEvent",
                repo = new { name = "org/repo" },
                payload = new { },
                created_at = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index)
            }))
        };

        var snapshot = await new DashboardService(api).RefreshAsync();

        Assert.Equal(Enumerable.Range(10, 30).Reverse().Select(index => index.ToString()),
            snapshot.Activity.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBecomingAnOfflineResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DashboardService(new FakeApi()).RefreshAsync(cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task SuccessfulRefreshPersistsEverySuccessfulSectionWithItsOriginalTimestamp()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var cache = new RecordingCacheStore();
        var api = new FakeApi();

        var snapshot = await new DashboardService(api, cache, clock).RefreshAsync();

        var record = Assert.Single(cache.Writes);
        Assert.Equal(new DashboardCacheAccount("github.com", 1, "octocat"), record.Account);
        Assert.Equal(snapshot.Activity.UpdatedAt, record.Activity!.SucceededAt);
        Assert.Equal(snapshot.PullRequests.UpdatedAt, record.PullRequests!.SucceededAt);
        Assert.Equal(snapshot.ReviewRequests.UpdatedAt, record.ReviewRequests!.SucceededAt);
        Assert.Equal(snapshot.Repositories.UpdatedAt, record.Repositories!.SucceededAt);
        Assert.Equal(snapshot.Contributions.UpdatedAt, record.Contributions!.SucceededAt);
        Assert.Equal(snapshot.Copilot.UpdatedAt, record.Copilot!.SucceededAt);
        Assert.Equal(snapshot.Activity.Items, record.Activity.Items);
        Assert.Equal(snapshot.PullRequests.Items, record.PullRequests.Items);
        Assert.Equal(snapshot.ReviewRequests.Items, record.ReviewRequests.Items);
        Assert.Equal(snapshot.Repositories.Items, record.Repositories.Items);
        Assert.Equal(snapshot.Contributions.Calendar!.TotalContributions,
            record.Contributions.Calendar.TotalContributions);
        Assert.Equal(
            snapshot.Contributions.Calendar.Weeks.SelectMany(week => week.Days),
            record.Contributions.Calendar.Weeks.SelectMany(week => week.Days));
        Assert.Equal(snapshot.Copilot.Usage!.Plan, record.Copilot.Usage.Plan);
        Assert.Equal(snapshot.Copilot.Usage.Quotas.AsEnumerable(), record.Copilot.Usage.Quotas.AsEnumerable());
        Assert.All(new[]
        {
            record.Activity.SucceededAt,
            record.PullRequests.SucceededAt,
            record.ReviewRequests.SucceededAt,
            record.Repositories.SucceededAt,
            record.Contributions.SucceededAt,
            record.Copilot.SucceededAt
        }, timestamp => Assert.Equal(now, timestamp));
    }

    [Fact]
    public async Task PartialFailureKeepsEligibleCachedSuccessWithoutRenewingItsTimestamp()
    {
        var clock = new ManualTimeProvider(new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
        var cache = new RecordingCacheStore();
        var api = new FakeApi();
        var service = new DashboardService(api, cache, clock);
        var previous = await service.RefreshAsync();
        var activityTimestamp = previous.Activity.UpdatedAt;
        clock.Advance(TimeSpan.FromHours(1));
        api.FailActivity = true;

        var refreshed = await service.RefreshAsync(previous);

        Assert.Equal(DashboardSectionSource.Retained, refreshed.Activity.Source);
        Assert.Equal(activityTimestamp, refreshed.Activity.UpdatedAt);
        var record = cache.Writes[^1];
        Assert.Equal(activityTimestamp, record.Activity!.SucceededAt);
        Assert.Equal(clock.GetUtcNow(), record.PullRequests!.SucceededAt);
    }

    [Fact]
    public async Task SuccessfulEmptySectionReplacesPersistedNonemptyDataAndAdvancesOnlyItsSuccess()
    {
        var clock = new ManualTimeProvider(new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
        var cache = new RecordingCacheStore();
        var api = new FakeApi();
        var service = new DashboardService(api, cache, clock);
        var previous = await service.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(30));
        api.ActivityResponse = "[]";

        var refreshed = await service.RefreshAsync(previous);

        Assert.Empty(refreshed.Activity.Items);
        var persisted = cache.Writes[^1].Activity!;
        Assert.Empty(persisted.Items);
        Assert.Equal(clock.GetUtcNow(), persisted.SucceededAt);
    }

    [Fact]
    public async Task InMemorySuccessAtExactRetentionBoundaryIsNotReusedOrPersisted()
    {
        var clock = new ManualTimeProvider(new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
        var cache = new RecordingCacheStore();
        var api = new FakeApi();
        var service = new DashboardService(api, cache, clock);
        var previous = await service.RefreshAsync();
        clock.Advance(TimeSpan.FromDays(7));
        api.FailActivity = true;

        var refreshed = await service.RefreshAsync(previous);

        Assert.Empty(refreshed.Activity.Items);
        Assert.Null(refreshed.Activity.UpdatedAt);
        Assert.Equal(DashboardSectionSource.Failed, refreshed.Activity.Source);
        Assert.Null(cache.Writes[^1].Activity);
    }

    [Fact]
    public async Task InitialIdentityFailureStillCompletesLocalCacheLookupWithoutWriting()
    {
        var api = new FakeApi { FailUser = true };
        var cache = new RecordingCacheStore();
        var service = new DashboardService(api, cache);

        DashboardSnapshot? hydrated = new DashboardSnapshot(
            new("github.com", 1, "sentinel", "Sentinel", new("https://github.com/sentinel")),
            new([], null, null), new([], null, null), new([], null, null), new([], null, null));
        await Assert.ThrowsAsync<GitHubException>(() =>
            service.RefreshWithHydrationAsync(null, value => hydrated = value));

        Assert.Null(hydrated);
        Assert.Equal(1, cache.ReadCount);
        Assert.Empty(cache.Writes);
        Assert.Equal(["user"], api.Endpoints);
    }

    [Fact]
    public async Task LastUsedCachePublishesBeforeAConfirmedDifferentAccount()
    {
        var clock = new ManualTimeProvider();
        var cache = new RecordingCacheStore
        {
            ReadRecord = new DashboardCacheRecord(
                DashboardCacheVersions.Schema,
                new("github.com", 2, "other"),
                new(DashboardCacheVersions.Activity, clock.GetUtcNow(), []),
                null, null, null, null, null)
        };
        DashboardSnapshot? hydrated = null;
        var publications = 0;
        var identityResponded = false;
        var api = new FakeApi { BeforeUser = () => identityResponded = true };

        var snapshot = await new DashboardService(api, cache, clock)
            .RefreshWithHydrationAsync(null, value =>
            {
                Assert.False(identityResponded);
                publications++;
                hydrated = value;
            });

        Assert.Equal(1, publications);
        Assert.Equal("other", hydrated!.User.Login);
        Assert.Equal("octocat", snapshot.User.Login);
        Assert.Equal(2, cache.ReadCount);
        Assert.Equal(1, Assert.Single(cache.Writes).Account.UserId);
    }

    [Fact]
    public async Task ClearCacheDelegatesWithoutRunningGitHubRequests()
    {
        var api = new FakeApi();
        var cache = new RecordingCacheStore
        {
            ClearResult = new(
                2,
                1,
                new(DashboardCacheDiagnosticKind.ClearFailed, "Fixture partial clear."))
        };

        var result = await new DashboardService(api, cache).ClearCacheAsync();

        Assert.Same(cache.ClearResult, result);
        Assert.Equal(1, cache.ClearCount);
        Assert.Empty(api.Endpoints);
        Assert.Empty(api.Queries);
    }

    [Fact]
    public async Task ClearCacheWithoutStorageIsASafeSuccessfulNoOp()
    {
        var result = await new DashboardService(new FakeApi()).ClearCacheAsync();
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.DeletedFileCount);
    }

    [Theory]
    [InlineData("https://evil.example/user")]
    [InlineData("--hostname=evil.example")]
    [InlineData("/user")]
    [InlineData("")]
    public async Task CliRejectsNonRelativeOrOptionEndpoints(string endpoint)
    {
        var factoryCalls = 0;
        var api = new GitHubCliApi(() =>
        {
            factoryCalls++;
            throw new InvalidOperationException("Must not prepare a process.");
        }, TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => api.GetAsync(endpoint));

        Assert.Equal("endpoint", error.ParamName);
        Assert.Equal(0, factoryCalls);
    }

    private sealed class FakeApi : IGitHubApi
    {
        public List<string> Endpoints { get; } = [];
        public List<string> Queries { get; } = [];
        public string Login { get; set; } = "octocat";
        public string? UserResponse { get; set; }
        public bool FailUser { get; set; }
        public bool FailActivity { get; set; }
        public bool FailRepositories { get; set; }
        public Action? AfterQuery { get; set; }
        public Action? AfterCopilot { get; set; }
        public Action? BeforeUser { get; set; }
        public string PullUrl { get; set; } = "https://github.com/octocat/tray/pull/42";
        public string RepositoryResponse { get; set; } =
            """[{"full_name":"octocat/tray","html_url":"https://github.com/octocat/tray","description":"A native tray app","language":"C#","private":true,"archived":false,"pushed_at":null,"updated_at":"2026-09-01T10:30:00Z"}]""";
        public string ActivityResponse { get; set; } =
            """[{"id":"1","type":"PushEvent","repo":{"name":"octocat/tray"},"payload":{"ref":"refs/heads/main"},"created_at":"2026-09-01T09:30:00Z"}]""";
        public int OperationCount => Endpoints.Count + Queries.Count;

        public void ClearRequests()
        {
            Endpoints.Clear();
            Queries.Clear();
        }

        public Task<string> QueryAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            if (query.Contains("query PullRequests", StringComparison.Ordinal))
            {
                var pulls = PullRequestTestData.Response(Login);
                PullRequestTestData.Pull(pulls)["url"] = PullUrl;
                return Task.FromResult(pulls.ToJsonString());
            }
            var response = ContributionTestData.Response(Login).ToJsonString();
            AfterQuery?.Invoke();
            return Task.FromResult(response);
        }

        public Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Endpoints.Add(endpoint);
            if (endpoint == CopilotUsageParser.Endpoint)
            {
                var response = CopilotTestData.Response(Login).ToJsonString();
                AfterCopilot?.Invoke();
                return Task.FromResult(response);
            }
            if (endpoint == "user")
            {
                BeforeUser?.Invoke();
                if (FailUser)
                {
                    throw new GitHubException("Signed out");
                }
                return Task.FromResult(UserResponse ?? JsonSerializer.Serialize(new
                {
                    id = string.Equals(Login, "octocat", StringComparison.OrdinalIgnoreCase) ? 1 : 2,
                    login = Login, name = "The Octocat", html_url = $"https://github.com/{Login}"
                }));
            }
            if (endpoint.StartsWith("users/", StringComparison.Ordinal))
            {
                if (FailActivity)
                {
                    throw new GitHubException("Offline");
                }
                return Task.FromResult(ActivityResponse);
            }
            if (endpoint.StartsWith("user/repos?", StringComparison.Ordinal))
            {
                if (FailRepositories)
                {
                    throw new GitHubException("Offline");
                }
                return Task.FromResult(RepositoryResponse);
            }
            throw new InvalidOperationException($"Unexpected endpoint: {endpoint}");
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset? now = null) : TimeProvider
    {
        private DateTimeOffset _now = now ?? new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class RecordingCacheStore : IDashboardCacheStore
    {
        public int ReadCount { get; private set; }
        public int ClearCount { get; private set; }
        public DashboardCacheRecord? ReadRecord { get; init; }
        public DashboardCacheClearResult? ClearResult { get; init; }
        public Action<CancellationToken>? BeforeWrite { get; init; }
        public Action? BeforeRead { get; set; }
        public Exception? WriteException { get; init; }
        public List<DashboardCacheRecord> Writes { get; } = [];

        public Task<DashboardCacheReadResult> ReadLastUsedAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeRead?.Invoke();
            ReadCount++;
            return Task.FromResult(new DashboardCacheReadResult(
                ReadRecord,
                new(ReadRecord is null
                    ? DashboardCacheDiagnosticKind.Missing
                    : DashboardCacheDiagnosticKind.Loaded, "Fixture last-used cache read.")));
        }

        public Task<DashboardCacheReadResult> ReadAsync(
            DashboardCacheAccount account,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeRead?.Invoke();
            ReadCount++;
            var record = ReadRecord is { } candidate &&
                string.Equals(candidate.Account.Host, account.Host, StringComparison.OrdinalIgnoreCase) &&
                candidate.Account.UserId == account.UserId
                    ? candidate
                    : null;
            return Task.FromResult(new DashboardCacheReadResult(
                record,
                new(record is null
                    ? DashboardCacheDiagnosticKind.Missing
                    : DashboardCacheDiagnosticKind.Loaded, "Fixture cache read.")));
        }

        public Task<DashboardCacheWriteResult> WriteAsync(
            DashboardCacheRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeWrite?.Invoke(cancellationToken);
            if (WriteException is not null)
            {
                throw WriteException;
            }

            Writes.Add(record);
            return Task.FromResult(new DashboardCacheWriteResult(
                new(DashboardCacheDiagnosticKind.Written, "Fixture cache write.")));
        }

        public Task<DashboardCacheWriteResult> SelectAccountAsync(
            DashboardCacheAccount account,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DashboardCacheWriteResult(
                new(DashboardCacheDiagnosticKind.Written, "Fixture account selected.")));
        }

        public Task<DashboardCacheClearResult> ClearAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCount++;
            return Task.FromResult(ClearResult ?? new DashboardCacheClearResult(
                0,
                0,
                new(DashboardCacheDiagnosticKind.Cleared, "Fixture cache cleared.")));
        }
    }

}
