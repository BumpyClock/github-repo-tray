using System.Text.Json;
using GitHubTray.Core;
using GitHubTray.Core.Tests;

namespace GitHubTray.AppState.Tests;

internal sealed class RefreshSessionFixture : IAsyncDisposable
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly List<RequestGate> gates = [];

    internal RefreshSessionFixture(
        IDashboardCacheStore? cacheStore = null,
        MutableTimeProvider? clock = null)
        : this(cacheStore, null, null, clock)
    {
    }

    internal RefreshSessionFixture(
        DashboardSnapshot initialRecoverySnapshot,
        TimeSpan? refreshInterval = null,
        MutableTimeProvider? clock = null)
        : this(null, initialRecoverySnapshot, refreshInterval, clock)
    {
    }

    private RefreshSessionFixture(
        IDashboardCacheStore? cacheStore,
        DashboardSnapshot? initialRecoverySnapshot,
        TimeSpan? refreshInterval,
        MutableTimeProvider? clock)
    {
        Clock = clock ?? new MutableTimeProvider();
        Session = new DashboardRefreshSession(
            new DashboardService(Api, cacheStore, Clock),
            initialRecoverySnapshot,
            refreshInterval);
    }

    internal ScriptedGitHubApi Api { get; } = new();
    internal MutableTimeProvider Clock { get; }
    internal DashboardRefreshSession Session { get; }

    internal RequestGate Gate(bool ignoreCancellation = false)
    {
        var gate = new RequestGate(ignoreCancellation);
        gates.Add(gate);
        return gate;
    }

    internal Task RefreshAsync(DashboardRefreshReason reason = DashboardRefreshReason.Manual) =>
        Session.RefreshAsync(reason).WaitAsync(Timeout);

    public async ValueTask DisposeAsync()
    {
        // Release even cancellation-ignoring requests before draining a failed test.
        foreach (var gate in gates)
        {
            gate.Release();
        }
        await Session.ShutdownAsync().WaitAsync(Timeout);
        await Session.DisposeAsync().AsTask().WaitAsync(Timeout);
    }
}

internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset utcNow = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => utcNow;

    internal void Advance(TimeSpan duration) => utcNow += duration;
}

internal sealed class RequestGate(bool ignoreCancellation)
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task EnteredAsync() => entered.Task.WaitAsync(RefreshSessionFixture.Timeout);
    internal Task CanceledAsync() => canceled.Task.WaitAsync(RefreshSessionFixture.Timeout);
    internal Task ExitedAsync() => exited.Task.WaitAsync(RefreshSessionFixture.Timeout);
    internal void Release() => released.TrySetResult();

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => canceled.TrySetResult());
        entered.TrySetResult();
        try
        {
            await released.Task.WaitAsync(RefreshSessionFixture.Timeout,
                ignoreCancellation ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The wait's callback can unwind and dispose our earlier registration before it runs.
            canceled.TrySetResult();
            throw;
        }
        finally
        {
            exited.TrySetResult();
        }
    }
}

internal enum ApiRoute
{
    InitialUser,
    Activity,
    PullRequests,
    ReviewRequests,
    Repositories,
    Contributions,
    Copilot,
    FinalUser
}

internal sealed record ApiReply(string Body)
{
    internal Exception? Failure { get; init; }
    internal RequestGate? Gate { get; init; }
    internal bool FaultAsTask { get; init; }
    internal Action? BeforeSend { get; init; }
    internal Action<CancellationToken>? ObserveCancellation { get; init; }

    internal Task<string> SendAsync(CancellationToken cancellationToken)
    {
        BeforeSend?.Invoke();
        ObserveCancellation?.Invoke(cancellationToken);
        if (Gate is not null)
        {
            return SendGatedAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null)
        {
            if (FaultAsTask)
            {
                return Task.FromException<string>(Failure);
            }
            throw Failure;
        }
        return Task.FromResult(Body);
    }

    private async Task<string> SendGatedAsync(CancellationToken cancellationToken)
    {
        await Gate!.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Failure is not null)
        {
            throw Failure;
        }
        return Body;
    }
}

internal sealed class RefreshResponses
{
    internal ApiReply InitialUser { get; set; } = new("");
    internal ApiReply Activity { get; set; } = new("");
    internal ApiReply PullRequests { get; set; } = new("");
    internal ApiReply ReviewRequests { get; set; } = new("");
    internal ApiReply Repositories { get; set; } = new("");
    internal ApiReply Contributions { get; set; } = new("");
    internal ApiReply Copilot { get; set; } = new("");
    internal ApiReply FinalUser { get; set; } = new("");

    internal ApiReply For(ApiRoute route) => route switch
    {
        ApiRoute.InitialUser => InitialUser,
        ApiRoute.Activity => Activity,
        ApiRoute.PullRequests => PullRequests,
        ApiRoute.ReviewRequests => ReviewRequests,
        ApiRoute.Repositories => Repositories,
        ApiRoute.Contributions => Contributions,
        ApiRoute.Copilot => Copilot,
        ApiRoute.FinalUser => FinalUser,
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    internal static ApiReply User(string login) => new(JsonSerializer.Serialize(new
    {
        id = string.Equals(login, "octocat", StringComparison.OrdinalIgnoreCase) ? 1 : 2,
        login,
        name = $"Display {login}",
        html_url = $"https://github.com/{login}"
    }));

    internal static ApiReply Calendar(string login, bool allZero = false) =>
        new(ContributionTestData.Response(login, allZero: allZero).ToJsonString());

    internal static RefreshResponses Success(string login = "octocat", string revision = "first")
    {
        var repository = $"{login}/{revision}";
        var pull = new ApiReply(PullRequestTestData.Response(login, revision).ToJsonString());
        return new RefreshResponses
        {
            InitialUser = User(login),
            FinalUser = User(login),
            Activity = new ApiReply(JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = revision, type = "PushEvent", repo = new { name = repository },
                    payload = new { @ref = "refs/heads/main" }, created_at = "2026-09-01T09:30:00Z"
                }
            })),
            PullRequests = pull,
            ReviewRequests = pull,
            Repositories = new ApiReply(JsonSerializer.Serialize(new[]
            {
                new
                {
                    full_name = repository, html_url = $"https://github.com/{repository}",
                    description = $"Private {revision}", language = "C#", @private = true,
                    archived = false, updated_at = "2026-09-01T10:30:00Z"
                }
            })),
            Contributions = Calendar(login),
            Copilot = new ApiReply(CopilotTestData.Response(login).ToJsonString())
        };
    }

    internal void FailSections(string message = "Offline")
    {
        Activity = Activity with { Failure = new GitHubException(message) };
        PullRequests = PullRequests with { Failure = new GitHubException(message) };
        ReviewRequests = ReviewRequests with { Failure = new GitHubException(message) };
        Repositories = Repositories with { Failure = new GitHubException(message) };
        Contributions = Contributions with { Failure = new GitHubException(message) };
        Copilot = Copilot with { Failure = new GitHubException(message) };
    }
}

internal sealed record ApiRequest(ApiRoute Route, string Target);

internal sealed class ScriptedGitHubApi : IGitHubApi
{
    private readonly object sync = new();
    private readonly List<ApiRequest> requests = [];
    private readonly HashSet<ApiRoute> usedRoutes = [];
    private RefreshResponses responses = RefreshResponses.Success();
    private RefreshResponses[] responseSequence = [RefreshResponses.Success()];
    private int responseIndex;
    private int userCalls;

    internal ApiRequest[] Requests
    {
        get
        {
            lock (sync)
            {
                return requests.ToArray();
            }
        }
    }

    internal void Use(params RefreshResponses[] next)
    {
        if (next.Length == 0)
        {
            throw new ArgumentException("At least one response round is required.", nameof(next));
        }

        lock (sync)
        {
            responseSequence = next;
            responseIndex = 0;
            responses = responseSequence[0];
            userCalls = 0;
            usedRoutes.Clear();
        }
    }

    public Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        ApiReply reply;
        lock (sync)
        {
            if (endpoint == "user" && userCalls == 2)
            {
                AdvanceRound();
            }
            var route = endpoint switch
            {
                "user" => userCalls++ == 0 ? ApiRoute.InitialUser : ApiRoute.FinalUser,
                CopilotUsageParser.Endpoint => ApiRoute.Copilot,
                _ when endpoint.StartsWith("users/", StringComparison.Ordinal) => ApiRoute.Activity,
                _ when endpoint.StartsWith("user/repos?", StringComparison.Ordinal) => ApiRoute.Repositories,
                _ => throw new InvalidOperationException($"Unexpected endpoint: {endpoint}")
            };
            reply = Record(route, endpoint);
        }
        return reply.SendAsync(cancellationToken);
    }

    private void AdvanceRound()
    {
        if (responseIndex + 1 < responseSequence.Length)
        {
            responses = responseSequence[++responseIndex];
        }
        userCalls = 0;
        usedRoutes.Clear();
    }

    public Task<string> QueryAsync(string query, CancellationToken cancellationToken = default)
    {
        ApiReply reply;
        lock (sync)
        {
            var route = query.Contains("query PullRequests", StringComparison.Ordinal)
                ? query.Contains("review-requested:", StringComparison.Ordinal) ? ApiRoute.ReviewRequests : ApiRoute.PullRequests
                : ApiRoute.Contributions;
            reply = Record(route, query);
        }
        return reply.SendAsync(cancellationToken);
    }

    private ApiReply Record(ApiRoute route, string target)
    {
        requests.Add(new ApiRequest(route, target));
        if (!usedRoutes.Add(route))
        {
            throw new InvalidOperationException($"Unexpected repeat request for {route}; no retry was scripted.");
        }
        return responses.For(route);
    }
}

internal sealed class MemoryDashboardCacheStore : IDashboardCacheStore
{
    private readonly object sync = new();
    private readonly Dictionary<(string Host, long UserId), DashboardCacheRecord> records = [];
    private DashboardCacheAccount? selectedAccount;

    internal int ReadCount { get; private set; }
    internal int WriteCount { get; private set; }
    internal int ClearCount { get; private set; }
    internal DashboardCacheRecord? LastWrite { get; private set; }
    internal RequestGate? ClearGate { get; set; }
    internal bool FailClear { get; set; }
    internal int RecordCount
    {
        get
        {
            lock (sync)
            {
                return records.Count;
            }
        }
    }

    public Task<DashboardCacheReadResult> ReadLastUsedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ReadCount++;
            DashboardCacheRecord? record = null;
            if (selectedAccount is not null)
            {
                records.TryGetValue(
                    (selectedAccount.Host.ToLowerInvariant(), selectedAccount.UserId),
                    out record);
            }
            return Task.FromResult(new DashboardCacheReadResult(
                record,
                new(record is null ? DashboardCacheDiagnosticKind.Missing : DashboardCacheDiagnosticKind.Loaded,
                    "Fixture last-used cache read.")));
        }
    }

    public Task<DashboardCacheReadResult> ReadAsync(
        DashboardCacheAccount account,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ReadCount++;
            records.TryGetValue((account.Host.ToLowerInvariant(), account.UserId), out var record);
            return Task.FromResult(new DashboardCacheReadResult(
                record,
                new(record is null ? DashboardCacheDiagnosticKind.Missing : DashboardCacheDiagnosticKind.Loaded,
                    "Fixture cache read.")));
        }
    }

    public Task<DashboardCacheWriteResult> WriteAsync(
        DashboardCacheRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            WriteCount++;
            LastWrite = record;
            var key = (record.Account.Host.ToLowerInvariant(), record.Account.UserId);
            if (record.Activity is null && record.PullRequests is null &&
                record.ReviewRequests is null && record.Repositories is null &&
                record.Contributions is null && record.Copilot is null)
            {
                records.Remove(key);
            }
            else
            {
                records[key] = record;
            }
        }
        return Task.FromResult(new DashboardCacheWriteResult(
            new(DashboardCacheDiagnosticKind.Written, "Fixture cache write.")));
    }

    public Task<DashboardCacheWriteResult> SelectAccountAsync(
        DashboardCacheAccount account,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            selectedAccount = account;
        }
        return Task.FromResult(new DashboardCacheWriteResult(
            new(DashboardCacheDiagnosticKind.Written, "Fixture account selected.")));
    }

    public async Task<DashboardCacheClearResult> ClearAsync(
        CancellationToken cancellationToken = default)
    {
        ClearCount++;
        if (ClearGate is not null)
        {
            await ClearGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        int deleted;
        lock (sync)
        {
            if (FailClear)
            {
                return new(
                    0,
                    records.Count,
                    new(DashboardCacheDiagnosticKind.ClearFailed, "Fixture cache deletion failed."));
            }
            deleted = records.Count;
            records.Clear();
            selectedAccount = null;
        }
        return new(
            deleted,
            0,
            new(DashboardCacheDiagnosticKind.Cleared, "Fixture cache cleared."));
    }
}

internal static class SessionAssertions
{
    internal static DashboardSnapshot Success(
        DashboardSessionState state, string login = "octocat", string revision = "first")
    {
        Assert.False(state.IsRefreshing);
        Assert.False(state.IsStopping);
        Assert.True(state.IsAccountVerified);
        Assert.Null(state.Error);
        Assert.Equal(login, state.LastKnownLogin);
        var snapshot = Assert.IsType<DashboardSnapshot>(state.Snapshot);
        Assert.Equal(login, snapshot.User.Login);
        Assert.Equal(revision, Assert.Single(snapshot.Activity.Items).Id);
        Assert.Equal($"{login}/{revision}", Assert.Single(snapshot.Activity.Items).Repository);
        Assert.Equal($"#42 Improve {revision}", Assert.Single(snapshot.PullRequests.Items).Title);
        Assert.Equal($"#42 Improve {revision}", Assert.Single(snapshot.ReviewRequests.Items).Title);
        Assert.Equal("Review requested", Assert.Single(snapshot.ReviewRequests.Items).Detail);
        Assert.Equal($"{login}/{revision}", Assert.Single(snapshot.Repositories.Items).Title);
        foreach (var section in Sections(snapshot))
        {
            Assert.NotNull(section.UpdatedAt);
            Assert.Null(section.Error);
            Assert.False(section.IsStale);
        }
        Assert.Equal(115, Assert.IsType<ContributionCalendar>(snapshot.Contributions.Calendar).TotalContributions);
        Assert.NotNull(snapshot.Contributions.UpdatedAt);
        Assert.Null(snapshot.Contributions.Error);
        Assert.False(snapshot.Contributions.IsStale);
        Assert.Equal(75.7, Assert.IsType<CopilotUsage>(snapshot.Copilot.Usage).Quotas[0].PercentRemaining);
        Assert.NotNull(snapshot.Copilot.UpdatedAt);
        Assert.Null(snapshot.Copilot.Error);
        Assert.False(snapshot.Copilot.IsStale);
        return snapshot;
    }

    internal static DashboardSection[] Sections(DashboardSnapshot snapshot) =>
        [snapshot.Activity, snapshot.PullRequests, snapshot.ReviewRequests, snapshot.Repositories];

    internal static void Unverified(DashboardSessionState state, string? lastKnownLogin)
    {
        Assert.False(state.IsAccountVerified);
        Assert.False(state.IsRefreshing);
        Assert.False(state.IsStopping);
        Assert.Equal(lastKnownLogin, state.LastKnownLogin);
        Assert.False(string.IsNullOrWhiteSpace(state.Error));
    }

    internal static void Retained(DashboardSnapshot previous, DashboardSnapshot current)
    {
        Assert.Equal(previous.User.Login, current.User.Login, ignoreCase: true);
        foreach (var (before, after) in Sections(previous).Zip(Sections(current)))
        {
            Assert.Equal(before.Items.ToArray(), after.Items.ToArray());
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.True(after.IsStale);
            Assert.Equal("Offline", after.Error);
        }
        Assert.Equal(previous.Contributions.UpdatedAt, current.Contributions.UpdatedAt);
        Assert.True(current.Contributions.IsStale);
        Assert.Equal("Offline", current.Contributions.Error);
        var oldCalendar = Assert.IsType<ContributionCalendar>(previous.Contributions.Calendar);
        var newCalendar = Assert.IsType<ContributionCalendar>(current.Contributions.Calendar);
        Assert.Equal(oldCalendar.TotalContributions, newCalendar.TotalContributions);
        Assert.Equal(oldCalendar.Weeks.Select(week => week.FirstDay), newCalendar.Weeks.Select(week => week.FirstDay));
        Assert.Equal(oldCalendar.Weeks.SelectMany(week => week.Days), newCalendar.Weeks.SelectMany(week => week.Days));
        Assert.Equal(previous.Copilot.Usage, current.Copilot.Usage);
        Assert.Equal(previous.Copilot.UpdatedAt, current.Copilot.UpdatedAt);
        Assert.True(current.Copilot.IsStale);
        Assert.Equal("Offline", current.Copilot.Error);
    }
}
