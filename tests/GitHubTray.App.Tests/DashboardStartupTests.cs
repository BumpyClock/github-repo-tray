using GitHubTray.AppState;
using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class DashboardStartupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void SecondaryLaunchDoesNotEvenConstructARefreshSession()
    {
        var sessionCreations = 0;
        var startup = DashboardStartup.StartIfPrimary(false, () =>
        {
            sessionCreations++;
            return new DashboardRefreshSession(new DashboardService(new StartupApi()));
        });

        Assert.Null(startup);
        Assert.Equal(0, sessionCreations);
    }

    [Fact]
    public async Task FirstRequestPrecedesWindowSetupAndCanCompleteWhileSetupIsBlocked()
    {
        var api = new StartupApi { IsInitialUserBlocked = true };
        await using var startup = Start(api);
        Assert.True(api.InitialUserEntered.Task.IsCompleted);
        Assert.Equal(1, api.RequestCount);
        Assert.Same(startup.RefreshTask, startup.Session.RefreshAsync());

        var setupEntered = Signal();
        using var releaseSetup = new ManualResetEventSlim();
        var window = new object();
        var construction = Task.Run(() => startup.CreateWindowAsync(initial =>
        {
            Assert.Same(startup, initial);
            Assert.True(api.InitialUserEntered.Task.IsCompleted);
            setupEntered.TrySetResult();
            if (!releaseSetup.Wait(Timeout))
                throw new TimeoutException("The test did not release window setup.");
            return window;
        }));
        try
        {
            await setupEntered.Task.WaitAsync(Timeout);
            api.ReleaseInitialUser.TrySetResult();
            await startup.RefreshTask.WaitAsync(Timeout);

            Assert.False(construction.IsCompleted);
            Assert.True(startup.Session.State.IsAccountVerified);
            Assert.Equal(8, api.RequestCount);
        }
        finally
        {
            releaseSetup.Set();
        }
        Assert.Same(window, await construction.WaitAsync(Timeout));
    }

    [Fact]
    public async Task RefreshCompletionIsProjectedWithoutWaitingForSettings()
    {
        var api = new StartupApi { IsInitialUserBlocked = true };
        await using var startup = Start(api);
        var settingsEntered = Signal();
        var releaseSettings = Signal();
        var published = new TaskCompletionSource<DashboardSessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialization = startup.InitializeAsync(
            () =>
            {
                settingsEntered.TrySetResult();
                return releaseSettings.Task;
            },
            state =>
            {
                if (state.IsAccountVerified)
                    published.TrySetResult(state);
            });
        try
        {
            await settingsEntered.Task.WaitAsync(Timeout);
            Assert.Equal(1, api.RequestCount);
            api.ReleaseInitialUser.TrySetResult();
            var state = await published.Task.WaitAsync(Timeout);

            Assert.NotNull(state.Snapshot);
            Assert.False(state.IsRefreshing);
            Assert.False(initialization.IsCompleted);
            Assert.Equal(8, api.RequestCount);
        }
        finally
        {
            releaseSettings.TrySetResult();
            await initialization.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task WindowConstructionReturnsImmediatelyWhileInitialRefreshIsPending()
    {
        var api = new StartupApi { IsInitialUserBlocked = true };
        await using var startup = Start(api);
        var window = new object();

        var construction = startup.CreateWindowAsync(_ => window);

        Assert.True(construction.IsCompletedSuccessfully);
        Assert.Same(window, await construction);
        Assert.False(startup.RefreshTask.IsCompleted);
        var states = new List<DashboardSessionState>();
        var initialization = startup.InitializeAsync(() => Task.CompletedTask, states.Add);
        Assert.False(initialization.IsCompleted);
        Assert.True(Assert.Single(states).IsRefreshing);
        api.ReleaseInitialUser.TrySetResult();
        await initialization.WaitAsync(Timeout);
        Assert.Equal(8, api.RequestCount);
    }

    [Fact]
    public async Task AlreadyCompletedPrefetchAndRepeatedInitializationDoNotStartAnotherFlight()
    {
        var api = new StartupApi();
        await using var startup = Start(api);
        var originalTask = startup.RefreshTask;
        await originalTask.WaitAsync(Timeout);
        var settingsCalls = 0;
        var states = new List<DashboardSessionState>();

        Task SettingsAsync()
        {
            settingsCalls++;
            return Task.CompletedTask;
        }
        var first = startup.InitializeAsync(SettingsAsync, states.Add);
        var second = startup.InitializeAsync(SettingsAsync, states.Add);
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        Assert.Same(originalTask, startup.RefreshTask);
        Assert.Same(first, second);
        Assert.Equal(1, settingsCalls);
        Assert.True(Assert.Single(states).IsAccountVerified);
        Assert.Equal(8, api.RequestCount);

        // This optimization must not turn the session into a permanent refresh cache.
        await startup.Session.RefreshAsync().WaitAsync(Timeout);
        Assert.Equal(16, api.RequestCount);
    }

    [Fact]
    public async Task IdentityFailureIsProjectedPromptlyRatherThanHiddenBehindSettings()
    {
        var api = new StartupApi { IsInitialUserBlocked = true, HasIdentityFailure = true };
        await using var startup = Start(api);
        var releaseSettings = Signal();
        var failed = new TaskCompletionSource<DashboardSessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialization = startup.InitializeAsync(() => releaseSettings.Task, state =>
        {
            if (state.Error is not null)
                failed.TrySetResult(state);
        });
        try
        {
            api.ReleaseInitialUser.TrySetResult();
            var state = await failed.Task.WaitAsync(Timeout);

            Assert.Null(state.Snapshot);
            Assert.False(state.IsAccountVerified);
            Assert.False(state.IsRefreshing);
            Assert.Contains("Fixture identity unavailable", state.Error);
            Assert.False(initialization.IsCompleted);
            Assert.Equal(1, api.RequestCount);
        }
        finally
        {
            releaseSettings.TrySetResult();
            await initialization.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task FailedWindowConstructionCancelsAndDrainsItsAcceptedFlight()
    {
        var api = new StartupApi { IsInitialUserBlocked = true };
        var startup = Start(api);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            startup.CreateWindowAsync<object>(_ => throw new InvalidOperationException("Window setup failed")));

        Assert.Equal("Window setup failed", exception.Message);
        Assert.True(api.CancellationObserved.Task.IsCompleted);
        Assert.True(startup.RefreshTask.IsCompletedSuccessfully);
        Assert.True(startup.Session.State.IsStopping);
        Assert.Null(startup.Session.State.Snapshot);
        await startup.Session.RefreshAsync().WaitAsync(Timeout);
        await startup.DisposeAsync();
        Assert.Equal(1, api.RequestCount);
    }

    [Fact]
    public async Task ShutdownWaitsForIgnoringTransportAndRejectsLateInitialPublication()
    {
        var api = new StartupApi { IsFinalUserBlocked = true, IsCancellationIgnored = true };
        var startup = Start(api);
        Task? initialization = null;
        Task? shutdown = null;
        var states = new List<DashboardSessionState>();
        try
        {
            await api.FinalUserEntered.Task.WaitAsync(Timeout);
            initialization = startup.InitializeAsync(() => Task.CompletedTask, states.Add);
            shutdown = startup.DisposeAsync().AsTask();
            await api.CancellationObserved.Task.WaitAsync(Timeout);

            Assert.True(startup.Session.State.IsStopping);
            Assert.Null(startup.Session.State.Snapshot);
            Assert.False(shutdown.IsCompleted);
            Assert.False(initialization.IsCompleted);
            var countAtShutdown = api.RequestCount;
            await startup.Session.RefreshAsync().WaitAsync(Timeout);
            Assert.Equal(countAtShutdown, api.RequestCount);

            api.ReleaseFinalUser.TrySetResult();
            await Task.WhenAll(shutdown, initialization).WaitAsync(Timeout);

            Assert.All(states, state => Assert.Null(state.Snapshot));
            Assert.True(states[^1].IsStopping);
            Assert.Null(startup.Session.State.Snapshot);
            Assert.Equal(8, api.RequestCount);
        }
        finally
        {
            api.ReleaseFinalUser.TrySetResult();
            await startup.DisposeAsync();
        }
    }

    [Fact]
    public async Task SettingsFailureDoesNotPreventObservationOfTheInitialResult()
    {
        var api = new StartupApi { IsInitialUserBlocked = true };
        await using var startup = Start(api);
        var states = new List<DashboardSessionState>();
        var initialization = startup.InitializeAsync(
            () => throw new IOException("Settings fixture failed"),
            states.Add);
        api.ReleaseInitialUser.TrySetResult();

        await Assert.ThrowsAsync<IOException>(() => initialization.WaitAsync(Timeout));

        Assert.True(states[^1].IsAccountVerified);
        Assert.Equal(8, api.RequestCount);
    }

    private static DashboardStartup Start(StartupApi api) =>
        DashboardStartup.StartIfPrimary(true,
            () => new DashboardRefreshSession(new DashboardService(api)))!;

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class StartupApi : IGitHubApi
    {
        private int _requestCount;
        private int _userRequestCount;

        public bool IsInitialUserBlocked { get; init; }
        public bool IsFinalUserBlocked { get; init; }
        public bool IsCancellationIgnored { get; init; }
        public bool HasIdentityFailure { get; init; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public TaskCompletionSource InitialUserEntered { get; } = Signal();
        public TaskCompletionSource FinalUserEntered { get; } = Signal();
        public TaskCompletionSource ReleaseInitialUser { get; } = Signal();
        public TaskCompletionSource ReleaseFinalUser { get; } = Signal();
        public TaskCompletionSource CancellationObserved { get; } = Signal();

        public async Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            if (endpoint == "user")
            {
                var isInitial = Interlocked.Increment(ref _userRequestCount) % 2 == 1;
                (isInitial ? InitialUserEntered : FinalUserEntered).TrySetResult();
                using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
                if (isInitial ? IsInitialUserBlocked : IsFinalUserBlocked)
                {
                    try
                    {
                        await (isInitial ? ReleaseInitialUser : ReleaseFinalUser).Task
                            .WaitAsync(IsCancellationIgnored ? CancellationToken.None : cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        CancellationObserved.TrySetResult();
                        throw;
                    }
                }
                if (HasIdentityFailure)
                    throw new GitHubException("Fixture identity unavailable");
                return """{"login":"octocat","name":"Octocat","html_url":"https://github.com/octocat"}""";
            }

            // Empty REST lists and unavailable optional sections still produce an eligible
            // snapshot after BOTH identity gates. This fixture never contacts GitHub.
            return "[]";
        }

        public Task<string> QueryAsync(string query, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult("{}");
        }
    }
}
