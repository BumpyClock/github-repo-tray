using System.Text.Json;
using GitHubTray.Core;
using GitHubTray.Core.Tests;

namespace GitHubTray.AppState.Tests;

internal sealed class RefreshSessionFixture : IAsyncDisposable
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly List<RequestGate> gates = [];

    internal RefreshSessionFixture()
    {
        Session = new DashboardRefreshSession(new DashboardService(Api));
    }

    internal ScriptedGitHubApi Api { get; } = new();
    internal DashboardRefreshSession Session { get; }

    internal RequestGate Gate(bool ignoreCancellation = false)
    {
        var gate = new RequestGate(ignoreCancellation);
        gates.Add(gate);
        return gate;
    }

    internal Task RefreshAsync() => Session.RefreshAsync().WaitAsync(Timeout);

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
    FinalUser
}

internal sealed record ApiReply(string Body)
{
    internal Exception? Failure { get; init; }
    internal RequestGate? Gate { get; init; }
    internal bool FaultAsTask { get; init; }
    internal Action? BeforeSend { get; init; }

    internal Task<string> SendAsync(CancellationToken cancellationToken)
    {
        BeforeSend?.Invoke();
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
    internal ApiReply FinalUser { get; set; } = new("");

    internal ApiReply For(ApiRoute route) => route switch
    {
        ApiRoute.InitialUser => InitialUser,
        ApiRoute.Activity => Activity,
        ApiRoute.PullRequests => PullRequests,
        ApiRoute.ReviewRequests => ReviewRequests,
        ApiRoute.Repositories => Repositories,
        ApiRoute.Contributions => Contributions,
        ApiRoute.FinalUser => FinalUser,
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    internal static ApiReply User(string login) => new(JsonSerializer.Serialize(new
    {
        login,
        name = $"Display {login}",
        html_url = $"https://github.com/{login}"
    }));

    internal static ApiReply Calendar(string login, bool allZero = false) =>
        new(ContributionTestData.Response(login, allZero: allZero).ToJsonString());

    internal static RefreshResponses Success(string login = "octocat", string revision = "first")
    {
        var repository = $"{login}/{revision}";
        var pull = new ApiReply(JsonSerializer.Serialize(new
        {
            incomplete_results = false,
            items = new[]
            {
                new
                {
                    number = 42, title = $"Improve {revision}",
                    html_url = $"https://github.com/{repository}/pull/42",
                    updated_at = "2026-09-01T10:30:00Z", draft = false
                }
            }
        }));
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
            Contributions = Calendar(login)
        };
    }

    internal void FailSections(string message = "Offline")
    {
        Activity = Activity with { Failure = new GitHubException(message) };
        PullRequests = PullRequests with { Failure = new GitHubException(message) };
        ReviewRequests = ReviewRequests with { Failure = new GitHubException(message) };
        Repositories = Repositories with { Failure = new GitHubException(message) };
        Contributions = Contributions with { Failure = new GitHubException(message) };
    }
}

internal sealed record ApiRequest(ApiRoute Route, string Target);

internal sealed class ScriptedGitHubApi : IGitHubApi
{
    private readonly object sync = new();
    private readonly List<ApiRequest> requests = [];
    private readonly HashSet<ApiRoute> usedRoutes = [];
    private RefreshResponses responses = RefreshResponses.Success();
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

    internal void Use(RefreshResponses next)
    {
        lock (sync)
        {
            responses = next;
            userCalls = 0;
            usedRoutes.Clear();
        }
    }

    public Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        ApiReply reply;
        lock (sync)
        {
            var route = endpoint switch
            {
                "user" => userCalls++ == 0 ? ApiRoute.InitialUser : ApiRoute.FinalUser,
                _ when endpoint.StartsWith("users/", StringComparison.Ordinal) => ApiRoute.Activity,
                _ when endpoint.StartsWith("user/repos?", StringComparison.Ordinal) => ApiRoute.Repositories,
                _ when endpoint.StartsWith("search/issues?", StringComparison.Ordinal) &&
                       Uri.UnescapeDataString(endpoint).Contains("review-requested:", StringComparison.Ordinal) =>
                    ApiRoute.ReviewRequests,
                _ when endpoint.StartsWith("search/issues?", StringComparison.Ordinal) &&
                       Uri.UnescapeDataString(endpoint).Contains("author:", StringComparison.Ordinal) =>
                    ApiRoute.PullRequests,
                _ => throw new InvalidOperationException($"Unexpected endpoint: {endpoint}")
            };
            reply = Record(route, endpoint);
        }
        return reply.SendAsync(cancellationToken);
    }

    public Task<string> QueryAsync(string query, CancellationToken cancellationToken = default)
    {
        ApiReply reply;
        lock (sync)
        {
            reply = Record(ApiRoute.Contributions, query);
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
        return snapshot;
    }

    internal static DashboardSection[] Sections(DashboardSnapshot snapshot) =>
        [snapshot.Activity, snapshot.PullRequests, snapshot.ReviewRequests, snapshot.Repositories];

    internal static void Unverified(DashboardSessionState state, string? lastKnownLogin)
    {
        Assert.Null(state.Snapshot);
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
    }
}
