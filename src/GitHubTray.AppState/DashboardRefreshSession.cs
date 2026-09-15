using System.Collections.Immutable;
using System.ComponentModel;
using GitHubTray.Core;

namespace GitHubTray.AppState;

/// <summary>Owns single-flight refresh, immutable publication, private recovery data, and shutdown.</summary>
public sealed class DashboardRefreshSession : IAsyncDisposable
{
    private const string UnexpectedRefreshError =
        "An unexpected refresh error occurred. Check GitHub CLI and try again.";

    private readonly object _gate = new();
    private readonly DashboardService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task<TimeSpan>? _startupRefreshInterval;
    private readonly TaskCompletionSource _initialHydration =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _initialVerification =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DashboardSessionState _state = new(null, null, null, false, false);
    private DashboardSnapshot? _recoverySnapshot;
    private Task? _refreshTask;
    private Task? _shutdownTask;
    private Task<DashboardCacheClearResult>? _clearTask;
    private Task? _postClearRefreshTask;
    private CancellationTokenSource? _refreshCancellation;
    private bool _hasStartedInitialRefresh;
    private TimeSpan _refreshInterval;
    private DashboardRefreshReason _activeReason;
    private bool _manualFollowUpRequested;
    private bool _forceNextRefresh;
    private long _generation;

    public DashboardRefreshSession(
        DashboardService service,
        DashboardSnapshot? initialRecoverySnapshot = null,
        TimeSpan? refreshInterval = null,
        Task<TimeSpan>? startupRefreshInterval = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMinutes(5);
        ValidateRefreshInterval(_refreshInterval);
        _startupRefreshInterval = startupRefreshInterval;
        _recoverySnapshot = initialRecoverySnapshot is null ? null : FreezeSnapshot(initialRecoverySnapshot);
    }

    /// <summary>Returns an immutable state object that remains unchanged by later session transitions.</summary>
    public DashboardSessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Completes after the first local cache lookup has published saved data or an explicit miss.
    /// No network identity request is required for completion.
    /// </summary>
    public Task InitialHydrationTask => _initialHydration.Task;

    /// <summary>
    /// Completes after the initial network identity request has either confirmed an account or failed.
    /// Any account-change publication precedes completion.
    /// </summary>
    public Task InitialVerificationTask => _initialVerification.Task;

    /// <summary>Raised after an accepted state transition has been published.</summary>
    public event Action? StateChanged;

    /// <summary>Updates the configured cadence used to judge startup Activity and PR freshness.</summary>
    public void SetRefreshInterval(TimeSpan refreshInterval)
    {
        ValidateRefreshInterval(refreshInterval);
        lock (_gate)
        {
            _refreshInterval = refreshInterval;
        }
    }

    /// <summary>Starts a refresh or returns the identical task for the refresh already in progress.</summary>
    /// <remarks>
    /// Loading is set synchronously. Read <see cref="State"/> after calling and again after awaiting;
    /// synchronous completion can already expose the final state. Publication precedes task completion.
    /// Expected GitHub and operating-system errors complete normally with failed state; other faults propagate.
    /// A manual request overlapping an automatic flight shares its task and adds at most one forced full
    /// follow-up when that automatic flight reused any sections.
    /// A manual request overlapping cache clearing shares one post-clear task that includes a forced full refresh.
    /// Once stopping, this returns a completed task without starting work.
    /// </remarks>
    public Task RefreshAsync(DashboardRefreshReason reason = DashboardRefreshReason.Manual)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        TaskCompletionSource completion;
        DashboardSnapshot? previous;
        TimeSpan refreshInterval;
        CancellationToken cancellationToken;
        CancellationTokenSource refreshCancellation;
        bool hydrateFromCache;
        long generation;
        lock (_gate)
        {
            if (_state.IsStopping)
            {
                return Task.CompletedTask;
            }

            if (_clearTask is { IsCompleted: false })
            {
                if (reason is DashboardRefreshReason.Manual)
                {
                    return _postClearRefreshTask ??= RefreshAfterClearAsync(_clearTask);
                }
                return _clearTask;
            }

            if (_refreshTask is not null)
            {
                if (reason is DashboardRefreshReason.Manual &&
                    _activeReason is not DashboardRefreshReason.Manual)
                {
                    _manualFollowUpRequested = true;
                }
                return _refreshTask;
            }

            if (_forceNextRefresh)
            {
                reason = DashboardRefreshReason.Manual;
                _forceNextRefresh = false;
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _refreshTask = completion.Task;
            _activeReason = reason;
            _manualFollowUpRequested = false;
            previous = _recoverySnapshot;
            refreshInterval = _refreshInterval;
            refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _refreshCancellation = refreshCancellation;
            cancellationToken = refreshCancellation.Token;
            hydrateFromCache = !_hasStartedInitialRefresh;
            _hasStartedInitialRefresh = true;
            generation = _generation;
            _state = new(
                _state.Snapshot,
                _state.LastKnownLogin,
                _state.Error,
                true,
                false,
                _state.Account,
                _state.VerificationStatus);
        }

        _ = RefreshCoreAsync(
            reason,
            previous,
            refreshInterval,
            hydrateFromCache,
            generation,
            refreshCancellation,
            cancellationToken,
            completion);
        StateChanged?.Invoke();
        return completion.Task;
    }

    /// <summary>
    /// Invalidates all reusable section data, clears every cache-store-owned account record,
    /// and forces the next accepted refresh to fetch every section.
    /// </summary>
    /// <remarks>
    /// The currently published snapshot remains displayable, but it is no longer recovery or
    /// freshness input. Repeated calls share one clear operation. Pre-clear refresh completion
    /// cannot publish or persist after this method advances the session generation.
    /// </remarks>
    public Task<DashboardCacheClearResult> ClearCacheAsync()
    {
        TaskCompletionSource<DashboardCacheClearResult> completion;
        Task refreshTask;
        CancellationTokenSource? refreshCancellation;
        long generation;
        lock (_gate)
        {
            if (_state.IsStopping)
            {
                return Task.FromResult(new DashboardCacheClearResult(
                    0,
                    0,
                    new(DashboardCacheDiagnosticKind.ClearFailed,
                        "Dashboard cache clearing was skipped because the session is stopping.")));
            }

            if (_clearTask is not null)
            {
                return _clearTask;
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _clearTask = completion.Task;
            _postClearRefreshTask = null;
            generation = ++_generation;
            _forceNextRefresh = true;
            _hasStartedInitialRefresh = true;
            _initialHydration.TrySetResult();
            _initialVerification.TrySetResult();
            _recoverySnapshot = null;
            _manualFollowUpRequested = false;
            _state = new(
                _state.Snapshot,
                _state.LastKnownLogin,
                null,
                false,
                false,
                _state.Account,
                _state.VerificationStatus);
            refreshTask = _refreshTask ?? Task.CompletedTask;
            refreshCancellation = _refreshCancellation;
        }

        _ = ClearCacheCoreAsync(
            generation,
            refreshTask,
            refreshCancellation,
            completion);
        StateChanged?.Invoke();
        return completion.Task;
    }

    private async Task RefreshAfterClearAsync(Task clearTask)
    {
        await clearTask.ConfigureAwait(false);
        await RefreshAsync(DashboardRefreshReason.Manual).ConfigureAwait(false);
    }

    /// <summary>Synchronously marks the session as stopping, then cancels and drains its accepted refresh.</summary>
    /// <remarks>
    /// Immediately clears displayable and recovery data, errors, and loading, retaining only last-known login metadata.
    /// No later completion publishes state. Repeated calls return the same shutdown task.
    /// Shutdown cancellation is not a refresh error; unexpected refresh or cancellation-callback faults propagate.
    /// </remarks>
    public Task ShutdownAsync()
    {
        TaskCompletionSource completion;
        Task refreshTask;
        Task clearTask;
        lock (_gate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = completion.Task;
            _state = new(
                null,
                _state.LastKnownLogin,
                null,
                false,
                true,
                _state.Account,
                DashboardAccountVerificationStatus.Failed);
            _recoverySnapshot = null;
            _manualFollowUpRequested = false;
            _forceNextRefresh = false;
            _generation++;
            refreshTask = _refreshTask ?? Task.CompletedTask;
            clearTask = _clearTask ?? Task.CompletedTask;
        }

        _ = ShutdownCoreAsync(refreshTask, clearTask, completion);
        StateChanged?.Invoke();
        return completion.Task;
    }

    /// <summary>Delegates to <see cref="ShutdownAsync"/>; repeated disposal shares the same shutdown completion.</summary>
    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task RefreshCoreAsync(
        DashboardRefreshReason reason,
        DashboardSnapshot? previous,
        TimeSpan refreshInterval,
        bool hydrateFromCache,
        long generation,
        CancellationTokenSource refreshCancellation,
        CancellationToken cancellationToken,
        TaskCompletionSource completion)
    {
        while (true)
        {
            DashboardRefreshResult? result = null;
            DashboardSnapshot? snapshot = null;
            string? error = null;
            Exception? unexpectedFault = null;
            GitHubUser? confirmedDifferentAccount = null;
            var completingInitialHydration = hydrateFromCache;
            var completingInitialVerification = hydrateFromCache;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new DashboardRefreshRequest(reason, refreshInterval);
                result = hydrateFromCache && reason is DashboardRefreshReason.Startup
                    ? await _service.RefreshWithHydrationAsync(
                        request,
                        previous,
                        hydrated => PublishHydrated(hydrated, generation, completion),
                        (user, cached) => PublishVerifiedAccount(
                            user, cached, generation, completion),
                        (user, message) =>
                        {
                            confirmedDifferentAccount = user;
                            PublishAccountMismatch(user, message, generation, completion);
                        },
                        ResolveStartupRefreshIntervalAsync,
                        cancellationToken).ConfigureAwait(false)
                    : hydrateFromCache
                    ? await _service.RefreshWithHydrationAsync(
                        request,
                        previous,
                        hydrated => PublishHydrated(hydrated, generation, completion),
                        (user, cached) => PublishVerifiedAccount(
                            user, cached, generation, completion),
                        (user, message) =>
                        {
                            confirmedDifferentAccount = user;
                            PublishAccountMismatch(user, message, generation, completion);
                        },
                        null,
                        cancellationToken).ConfigureAwait(false)
                    : await _service.RefreshAsync(
                        request,
                        previous,
                        (user, cached) => PublishVerifiedAccount(
                            user, cached, generation, completion),
                        (user, message) =>
                        {
                            confirmedDifferentAccount = user;
                            PublishAccountMismatch(user, message, generation, completion);
                        },
                        cancellationToken).ConfigureAwait(false);
                snapshot = FreezeSnapshot(result.Snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown already published the terminal state.
            }
            catch (GitHubException exception)
            {
                error = exception.Message;
            }
            catch (Exception exception) when (exception is IOException or Win32Exception or UnauthorizedAccessException)
            {
                error = UnexpectedRefreshError;
            }
            catch (Exception exception)
            {
                error = UnexpectedRefreshError;
                unexpectedFault = exception;
            }
            finally
            {
                hydrateFromCache = false;
                if (completingInitialHydration)
                {
                    _initialHydration.TrySetResult();
                }
                if (completingInitialVerification)
                {
                    _initialVerification.TrySetResult();
                }
            }

            var runManualFollowUp = false;
            var disposeRefreshCancellation = false;
            var stateChanged = false;
            lock (_gate)
            {
                if (!_state.IsStopping && generation == _generation)
                {
                    if (snapshot is not null)
                    {
                        _recoverySnapshot = snapshot;
                    }

                    runManualFollowUp =
                        reason is not DashboardRefreshReason.Manual &&
                        _manualFollowUpRequested &&
                        unexpectedFault is null &&
                        (snapshot is null || result!.ReusedAnySection ||
                         HasSectionFailures(snapshot));

                    if (runManualFollowUp)
                    {
                        reason = DashboardRefreshReason.Manual;
                        previous = snapshot ?? _recoverySnapshot;
                        refreshInterval = _refreshInterval;
                        _activeReason = reason;
                        _manualFollowUpRequested = false;
                        _state = snapshot is not null
                            ? new(
                                snapshot,
                                snapshot.User.Login,
                                null,
                                true,
                                false,
                                snapshot.User,
                                DashboardAccountVerificationStatus.Verified)
                            : FailedState(
                                error,
                                isRefreshing: true,
                                confirmedDifferentAccount is not null);
                    }
                    else
                    {
                        _state = snapshot is not null
                            ? new(
                                snapshot,
                                snapshot.User.Login,
                                null,
                                false,
                                false,
                                snapshot.User,
                                DashboardAccountVerificationStatus.Verified)
                            : FailedState(
                                error,
                                isRefreshing: false,
                                confirmedDifferentAccount is not null);
                    }
                    stateChanged = true;
                }

                if (!runManualFollowUp)
                {
                    // Asynchronous continuations let task completion and flight retirement be atomic under the gate.
                    if (unexpectedFault is not null)
                    {
                        completion.SetException(unexpectedFault);
                    }
                    else
                    {
                        completion.SetResult();
                    }

                    _refreshTask = null;
                    if (ReferenceEquals(_refreshCancellation, refreshCancellation))
                    {
                        _refreshCancellation = null;
                    }
                    disposeRefreshCancellation = _clearTask is null;
                    _manualFollowUpRequested = false;
                }
            }

            if (stateChanged)
            {
                StateChanged?.Invoke();
            }

            if (!runManualFollowUp)
            {
                if (disposeRefreshCancellation)
                {
                    refreshCancellation.Dispose();
                }
                return;
            }
        }
    }

    private async Task<TimeSpan> ResolveStartupRefreshIntervalAsync(CancellationToken cancellationToken)
    {
        TimeSpan refreshInterval;
        if (_startupRefreshInterval is not null)
        {
            refreshInterval = await _startupRefreshInterval.WaitAsync(cancellationToken).ConfigureAwait(false);
            ValidateRefreshInterval(refreshInterval);
            lock (_gate)
            {
                _refreshInterval = refreshInterval;
            }
            return refreshInterval;
        }

        lock (_gate)
        {
            return _refreshInterval;
        }
    }

    private void PublishHydrated(
        DashboardSnapshot? snapshot,
        long generation,
        TaskCompletionSource completion)
    {
        var frozen = snapshot is null ? null : FreezeSnapshot(snapshot);
        var stateChanged = false;
        lock (_gate)
        {
            if (frozen is not null && !_state.IsStopping && generation == _generation &&
                ReferenceEquals(_refreshTask, completion.Task))
            {
                _recoverySnapshot = frozen;
                _state = new(
                    frozen,
                    frozen.User.Login,
                    null,
                    true,
                    false,
                    frozen.User,
                    DashboardAccountVerificationStatus.Verifying);
                stateChanged = true;
            }
            _initialHydration.TrySetResult();
        }
        if (stateChanged)
        {
            StateChanged?.Invoke();
        }
    }

    private void PublishVerifiedAccount(
        GitHubUser user,
        DashboardSnapshot? verifiedSnapshot,
        long generation,
        TaskCompletionSource completion)
    {
        var frozen = verifiedSnapshot is null ? null : FreezeSnapshot(verifiedSnapshot);
        var stateChanged = false;
        lock (_gate)
        {
            if (!_state.IsStopping && generation == _generation &&
                ReferenceEquals(_refreshTask, completion.Task))
            {
                DashboardSnapshot? display = frozen ?? _state.Snapshot;
                if (display is not null && AccountsMatch(display.User, user))
                {
                    if (!Equals(display.User, user))
                    {
                        display = display with { User = user };
                    }
                    // A rendered snapshot can outlive clearing; identity alone cannot make it reusable.
                    if (frozen is not null)
                    {
                        _recoverySnapshot = display;
                    }
                }
                else if (display is not null)
                {
                    display = null;
                    _recoverySnapshot = null;
                }

                _state = new(
                    display,
                    user.Login,
                    null,
                    true,
                    false,
                    user,
                    DashboardAccountVerificationStatus.Verified);
                stateChanged = true;
            }
            _initialVerification.TrySetResult();
        }
        if (stateChanged)
        {
            StateChanged?.Invoke();
        }
    }

    private void PublishAccountMismatch(
        GitHubUser? confirmedAccount,
        string message,
        long generation,
        TaskCompletionSource completion)
    {
        var stateChanged = false;
        lock (_gate)
        {
            if (!_state.IsStopping && generation == _generation &&
                ReferenceEquals(_refreshTask, completion.Task))
            {
                _recoverySnapshot = null;
                _state = new(
                    null,
                    confirmedAccount?.Login ?? _state.LastKnownLogin,
                    message,
                    true,
                    false,
                    confirmedAccount ?? _state.Account,
                    confirmedAccount is null
                        ? DashboardAccountVerificationStatus.Failed
                        : DashboardAccountVerificationStatus.Verified);
                stateChanged = true;
            }
            _initialVerification.TrySetResult();
        }
        if (stateChanged)
        {
            StateChanged?.Invoke();
        }
    }

    private DashboardSessionState FailedState(
        string? error,
        bool isRefreshing,
        bool confirmedDifferentAccount)
    {
        return new(
            _state.Snapshot,
            _state.Account?.Login ?? _state.LastKnownLogin,
            error,
            isRefreshing,
            false,
            _state.Account,
            confirmedDifferentAccount
                ? DashboardAccountVerificationStatus.Verified
                : DashboardAccountVerificationStatus.Failed);
    }

    private async Task ClearCacheCoreAsync(
        long generation,
        Task refreshTask,
        CancellationTokenSource? refreshCancellation,
        TaskCompletionSource<DashboardCacheClearResult> completion)
    {
        DashboardCacheClearResult result;
        Exception? cancellationFailure = null;
        try
        {
            var cancellation = refreshCancellation?.CancelAsync() ?? Task.CompletedTask;
            var clearing = _service.ClearCacheAsync(_lifetime.Token);
            try
            {
                await cancellation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }
            try
            {
                await refreshTask.ConfigureAwait(false);
            }
            catch
            {
                // The refresh task remains observable by its original callers; clearing is independent.
            }
            result = await clearing.ConfigureAwait(false);
            if (cancellationFailure is not null)
            {
                result = result with
                {
                    Diagnostic = new(
                        DashboardCacheDiagnosticKind.ClearFailed,
                        result.Succeeded
                            ? "Dashboard cache was cleared, but the in-progress refresh could not be canceled cleanly."
                            : $"{result.Diagnostic.Message} The in-progress refresh also could not be canceled cleanly.")
                };
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            result = new(
                0,
                0,
                new(DashboardCacheDiagnosticKind.ClearFailed,
                    "Dashboard cache clearing was interrupted by shutdown."));
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or
                                         UnauthorizedAccessException or System.Security.SecurityException)
        {
            result = new(
                0,
                1,
                new(DashboardCacheDiagnosticKind.ClearFailed,
                    "Dashboard cache could not be cleared. GitHub sign-in and settings were preserved."));
        }
        catch (Exception)
        {
            result = new(
                0,
                1,
                new(DashboardCacheDiagnosticKind.ClearFailed,
                    "An unexpected cache clearing error occurred. GitHub sign-in and settings were preserved."));
        }
        finally
        {
            try
            {
                await refreshTask.ConfigureAwait(false);
            }
            catch
            {
                // The refresh task remains observable by its original callers.
            }
            refreshCancellation?.Dispose();
        }

        var stateChanged = false;
        lock (_gate)
        {
            if (!_state.IsStopping && generation == _generation)
            {
                _state = new(
                    _state.Snapshot,
                    _state.LastKnownLogin,
                    null,
                    false,
                    false,
                    _state.Account,
                    _state.VerificationStatus);
                stateChanged = true;
            }
            completion.SetResult(result);
            if (ReferenceEquals(_clearTask, completion.Task))
            {
                _clearTask = null;
            }
        }
        if (stateChanged)
        {
            StateChanged?.Invoke();
        }
    }

    private async Task ShutdownCoreAsync(
        Task refreshTask,
        Task clearTask,
        TaskCompletionSource completion)
    {
        try
        {
            using (_lifetime)
            {
                // Drain the refresh even if a cancellation callback faults; never run callbacks under the gate.
                await Task.WhenAll(_lifetime.CancelAsync(), refreshTask, clearTask).ConfigureAwait(false);
            }

            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    internal static DashboardSnapshot FreezeSnapshot(DashboardSnapshot snapshot)
    {
        var activity = FreezeSection(snapshot.Activity);
        var pullRequests = FreezeSection(snapshot.PullRequests);
        var reviewRequests = FreezeSection(snapshot.ReviewRequests);
        var repositories = FreezeSection(snapshot.Repositories);
        var contributions = FreezeContributions(snapshot.Contributions);
        if (ReferenceEquals(activity, snapshot.Activity) &&
            ReferenceEquals(pullRequests, snapshot.PullRequests) &&
            ReferenceEquals(reviewRequests, snapshot.ReviewRequests) &&
            ReferenceEquals(repositories, snapshot.Repositories) &&
            ReferenceEquals(contributions, snapshot.Contributions))
        {
            return snapshot;
        }

        return snapshot with
        {
            Activity = activity,
            PullRequests = pullRequests,
            ReviewRequests = reviewRequests,
            Repositories = repositories,
            Contributions = contributions
        };
    }

    private static DashboardSection FreezeSection(DashboardSection section) =>
        section.Items is ImmutableArray<DashboardItem>
            ? section
            : section with { Items = section.Items.ToImmutableArray() };

    private static ContributionSection FreezeContributions(ContributionSection section)
    {
        if (section.Calendar is not { } calendar)
        {
            return section;
        }

        var weeksAreImmutable = calendar.Weeks is ImmutableArray<ContributionWeek>;
        var allDaysAreImmutable = true;
        for (var index = 0; index < calendar.Weeks.Count; index++)
        {
            if (calendar.Weeks[index].Days is not ImmutableArray<ContributionDay>)
            {
                allDaysAreImmutable = false;
                break;
            }
        }

        if (weeksAreImmutable && allDaysAreImmutable)
        {
            return section;
        }

        var weeks = calendar.Weeks
            .Select(week => week.Days is ImmutableArray<ContributionDay>
                ? week
                : week with { Days = week.Days.ToImmutableArray() })
            .ToImmutableArray();
        return section with { Calendar = calendar with { Weeks = weeks } };
    }

    private static bool HasSectionFailures(DashboardSnapshot snapshot) =>
        snapshot.Activity.Error is not null ||
        snapshot.PullRequests.Error is not null ||
        snapshot.ReviewRequests.Error is not null ||
        snapshot.Repositories.Error is not null ||
        snapshot.Contributions.Error is not null ||
        snapshot.Copilot.Error is not null;

    private static bool AccountsMatch(GitHubUser expected, GitHubUser actual) =>
        string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase) &&
        expected.Id == actual.Id &&
        string.Equals(expected.Login, actual.Login, StringComparison.OrdinalIgnoreCase);

    private static void ValidateRefreshInterval(TimeSpan refreshInterval)
    {
        if (refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval),
                "Refresh interval must be positive.");
        }
    }
}
