using GitHubTray.Core;

namespace GitHubTray.AppState.Tests;

public sealed class DashboardRefreshSessionClearTests
{
    [Fact]
    public async Task ClearKeepsCurrentRenderButForcesNextPeriodicRefreshToFetchAllSections()
    {
        var cache = new MemoryDashboardCacheStore();
        await using var fixture = new RefreshSessionFixture(cache);
        await fixture.RefreshAsync();
        var rendered = SessionAssertions.Success(fixture.Session.State);
        Assert.Equal(1, cache.RecordCount);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await fixture.Session.ClearCacheAsync();

        Assert.True(result.Succeeded);
        Assert.Same(rendered, fixture.Session.State.Snapshot);
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.False(fixture.Session.State.IsRefreshing);
        Assert.Equal(0, cache.RecordCount);

        fixture.Api.Use(RefreshResponses.Success(revision: "post-clear"));
        var before = fixture.Api.Requests.Length;
        await fixture.RefreshAsync(DashboardRefreshReason.Periodic);

        SessionAssertions.Success(fixture.Session.State, revision: "post-clear");
        Assert.Equal(8, fixture.Api.Requests.Length - before);
        Assert.Equal(1, cache.RecordCount);
        Assert.Equal("post-clear", Assert.Single(cache.LastWrite!.Activity!.Items).Id);
    }

    [Fact]
    public async Task ClearCancelsAndFencesARefreshCompletionThatStartedBeforeInvalidation()
    {
        var cache = new MemoryDashboardCacheStore();
        await using var fixture = new RefreshSessionFixture(cache);
        await fixture.RefreshAsync();
        var rendered = SessionAssertions.Success(fixture.Session.State);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var finalGate = fixture.Gate(ignoreCancellation: true);
        var responses = RefreshResponses.Success(revision: "obsolete");
        responses.FinalUser = responses.FinalUser with { Gate = finalGate };
        fixture.Api.Use(responses);
        var refresh = fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic);
        await finalGate.EnteredAsync();

        var clear = fixture.Session.ClearCacheAsync();
        await finalGate.CanceledAsync();
        Assert.Same(rendered, fixture.Session.State.Snapshot);
        Assert.False(fixture.Session.State.IsRefreshing);
        Assert.False(clear.IsCompleted);

        finalGate.Release();
        await Task.WhenAll(refresh, clear).WaitAsync(RefreshSessionFixture.Timeout);

        Assert.Same(rendered, fixture.Session.State.Snapshot);
        Assert.Equal("first", Assert.Single(fixture.Session.State.Snapshot!.Activity.Items).Id);
        Assert.Equal(0, cache.RecordCount);
    }

    [Fact]
    public async Task ClearDrainsRefreshAndStoreClearWhenCancellationCallbackFaults()
    {
        var cache = new MemoryDashboardCacheStore();
        await using var fixture = new RefreshSessionFixture(cache);
        await fixture.RefreshAsync();
        var finalGate = fixture.Gate(ignoreCancellation: true);
        var clearGate = fixture.Gate();
        cache.ClearGate = clearGate;
        var responses = RefreshResponses.Success(revision: "obsolete");
        responses.FinalUser = responses.FinalUser with
        {
            Gate = finalGate,
            ObserveCancellation = token =>
                token.Register(() => throw new InvalidOperationException("Injected callback failure"))
        };
        fixture.Api.Use(responses, RefreshResponses.Success(revision: "post-clear"));
        var refresh = fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic);
        await finalGate.EnteredAsync();

        var clear = fixture.Session.ClearCacheAsync();
        await finalGate.CanceledAsync();
        await clearGate.EnteredAsync();
        Assert.NotSame(clear, await Task.WhenAny(clear, Task.Delay(200)));

        finalGate.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);
        Assert.False(clear.IsCompleted);
        var requestsBeforePostClear = fixture.Api.Requests.Length;
        var manual = fixture.Session.RefreshAsync(DashboardRefreshReason.Manual);
        Assert.False(manual.IsCompleted);
        Assert.Equal(requestsBeforePostClear, fixture.Api.Requests.Length);

        clearGate.Release();
        var result = await clear.WaitAsync(RefreshSessionFixture.Timeout);
        await manual.WaitAsync(RefreshSessionFixture.Timeout);

        Assert.False(result.Succeeded);
        Assert.Contains("could not be canceled cleanly", result.Diagnostic.Message);
        Assert.False(fixture.Session.State.IsRefreshing);
        Assert.Equal(8, fixture.Api.Requests.Length - requestsBeforePostClear);
        Assert.Equal(1, cache.RecordCount);
        Assert.Equal("post-clear", Assert.Single(cache.LastWrite!.Activity!.Items).Id);
    }

    [Fact]
    public async Task FailedPostClearRefreshCannotRepopulateOrBecomeFreshnessInput()
    {
        var cache = new MemoryDashboardCacheStore();
        await using var fixture = new RefreshSessionFixture(cache);
        await fixture.RefreshAsync();
        await fixture.Session.ClearCacheAsync();
        var failed = RefreshResponses.Success(revision: "failed");
        failed.FailSections();
        fixture.Api.Use(failed);
        var before = fixture.Api.Requests.Length;

        await fixture.RefreshAsync(DashboardRefreshReason.Periodic);

        Assert.Equal(8, fixture.Api.Requests.Length - before);
        Assert.Equal(0, cache.RecordCount);
        Assert.All(SessionAssertions.Sections(fixture.Session.State.Snapshot!), section =>
        {
            Assert.Null(section.UpdatedAt);
            Assert.Equal(DashboardSectionSource.Failed, section.Source);
        });

        fixture.Api.Use(RefreshResponses.Success(revision: "recovered"));
        before = fixture.Api.Requests.Length;
        await fixture.RefreshAsync(DashboardRefreshReason.Periodic);

        Assert.Equal(8, fixture.Api.Requests.Length - before);
        Assert.Equal(1, cache.RecordCount);
        SessionAssertions.Success(fixture.Session.State, revision: "recovered");
    }

    [Fact]
    public async Task CacheReadCompletingAfterClearCannotPublishOrRecreateData()
    {
        DashboardCacheRecord cached;
        var seedCache = new MemoryDashboardCacheStore();
        await using (var seed = new RefreshSessionFixture(seedCache))
        {
            await seed.RefreshAsync();
            cached = Assert.IsType<DashboardCacheRecord>(seedCache.LastWrite);
        }

        var delayed = new DelayedReadCacheStore(cached);
        await using var fixture = new RefreshSessionFixture(delayed);
        fixture.Api.Use(RefreshResponses.Success(revision: "obsolete"));
        var refresh = fixture.Session.RefreshAsync(DashboardRefreshReason.Startup);
        await delayed.ReadEntered.Task.WaitAsync(RefreshSessionFixture.Timeout);

        var clear = fixture.Session.ClearCacheAsync();
        await delayed.ClearEntered.Task.WaitAsync(RefreshSessionFixture.Timeout);
        delayed.ReleaseRead.TrySetResult();
        await Task.WhenAll(refresh, clear).WaitAsync(RefreshSessionFixture.Timeout);

        Assert.Null(fixture.Session.State.Snapshot);
        Assert.False(fixture.Session.State.IsAccountVerified);
        Assert.Equal(0, delayed.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityFailureAfterClearCannotRestoreInvalidatedRecovery(bool deletionFails)
    {
        var cache = new MemoryDashboardCacheStore();
        await using var fixture = new RefreshSessionFixture(cache);
        await fixture.RefreshAsync();
        var rendered = fixture.Session.State.Snapshot;
        cache.FailClear = deletionFails;
        var cleared = await fixture.Session.ClearCacheAsync();
        Assert.Equal(!deletionFails, cleared.Succeeded);
        var readsAfterClear = cache.ReadCount;
        var writesAfterClear = cache.WriteCount;

        var unverified = RefreshResponses.Success(revision: "must-not-publish");
        unverified.FinalUser = unverified.FinalUser with
        {
            Failure = new GitHubException("Final identity unavailable")
        };
        fixture.Api.Use(unverified);
        await fixture.RefreshAsync(DashboardRefreshReason.Periodic);

        Assert.Same(rendered, fixture.Session.State.Snapshot);
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.Equal(readsAfterClear, cache.ReadCount);
        Assert.Equal(writesAfterClear, cache.WriteCount);

        var failedSections = RefreshResponses.Success(revision: "failed");
        failedSections.FailSections();
        fixture.Api.Use(failedSections);
        var before = fixture.Api.Requests.Length;
        await fixture.RefreshAsync(DashboardRefreshReason.Periodic);

        Assert.Equal(8, fixture.Api.Requests.Length - before);
        Assert.Equal(readsAfterClear, cache.ReadCount);
        Assert.Equal(0, cache.RecordCount);
        Assert.All(SessionAssertions.Sections(fixture.Session.State.Snapshot!), section =>
        {
            Assert.Empty(section.Items);
            Assert.Null(section.UpdatedAt);
        });
        Assert.Null(fixture.Session.State.Snapshot!.Contributions.Calendar);
        Assert.Null(fixture.Session.State.Snapshot.Copilot.Usage);
    }

    [Fact]
    public async Task RepeatedClearRequestsCoalesceAndShutdownSafelyInterruptsTheOperation()
    {
        var cache = new MemoryDashboardCacheStore();
        await using var fixture = new RefreshSessionFixture(cache);
        await fixture.RefreshAsync();
        var clearGate = fixture.Gate();
        cache.ClearGate = clearGate;

        var first = fixture.Session.ClearCacheAsync();
        await clearGate.EnteredAsync();
        var second = fixture.Session.ClearCacheAsync();
        Assert.Same(first, second);
        Assert.Equal(1, cache.ClearCount);

        var shutdown = fixture.Session.ShutdownAsync();
        await clearGate.CanceledAsync();
        var result = await first.WaitAsync(RefreshSessionFixture.Timeout);
        await shutdown.WaitAsync(RefreshSessionFixture.Timeout);

        Assert.False(result.Succeeded);
        Assert.True(fixture.Session.State.IsStopping);
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.True(fixture.Session.ClearCacheAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ManualRefreshDuringDelayedClearRunsOneForcedFullRefreshAfterClear()
    {
        var cache = new MemoryDashboardCacheStore();
        await using var fixture = new RefreshSessionFixture(cache);
        await fixture.RefreshAsync();
        var clearGate = fixture.Gate();
        cache.ClearGate = clearGate;
        fixture.Api.Use(RefreshResponses.Success(revision: "post-clear-manual"));
        var before = fixture.Api.Requests.Length;

        var clear = fixture.Session.ClearCacheAsync();
        await clearGate.EnteredAsync();
        var manual = fixture.Session.RefreshAsync(DashboardRefreshReason.Manual);
        Assert.NotSame(clear, manual);
        Assert.Same(manual, fixture.Session.RefreshAsync(DashboardRefreshReason.Manual));
        Assert.False(manual.IsCompleted);
        Assert.Equal(before, fixture.Api.Requests.Length);

        clearGate.Release();
        await manual.WaitAsync(RefreshSessionFixture.Timeout);
        var clearResult = await clear.WaitAsync(RefreshSessionFixture.Timeout);

        Assert.True(clearResult.Succeeded);
        Assert.Equal(8, fixture.Api.Requests.Length - before);
        SessionAssertions.Success(fixture.Session.State, revision: "post-clear-manual");
        Assert.Equal("post-clear-manual", Assert.Single(cache.LastWrite!.Activity!.Items).Id);
    }

    private sealed class DelayedReadCacheStore(DashboardCacheRecord record) : IDashboardCacheStore
    {
        private DashboardCacheRecord? _record = record;

        internal TaskCompletionSource ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ClearEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WriteCount { get; private set; }

        public async Task<DashboardCacheReadResult> ReadLastUsedAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            var stale = _record;
            ReadEntered.TrySetResult();
            await ReleaseRead.Task.WaitAsync(RefreshSessionFixture.Timeout).ConfigureAwait(false);
            return new(
                stale,
                new(DashboardCacheDiagnosticKind.Loaded, "Delayed fixture cache read."));
        }

        public Task<DashboardCacheReadResult> ReadAsync(
            DashboardCacheAccount account,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DashboardCacheReadResult(
                _record,
                new(_record is null
                    ? DashboardCacheDiagnosticKind.Missing
                    : DashboardCacheDiagnosticKind.Loaded,
                    "Fixture cache read.")));
        }

        public Task<DashboardCacheWriteResult> WriteAsync(
            DashboardCacheRecord replacement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            _record = replacement;
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
            ClearEntered.TrySetResult();
            _record = null;
            return Task.FromResult(new DashboardCacheClearResult(
                1,
                0,
                new(DashboardCacheDiagnosticKind.Cleared, "Fixture cache cleared.")));
        }
    }
}
