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
    private DashboardSessionState _state = new(null, null, null, false, false);
    private DashboardSnapshot? _recoverySnapshot;
    private Task? _refreshTask;
    private Task? _shutdownTask;

    public DashboardRefreshSession(DashboardService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
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

    /// <summary>Starts a refresh or returns the identical task for the refresh already in progress.</summary>
    /// <remarks>
    /// Loading is set synchronously. Read <see cref="State"/> after calling and again after awaiting;
    /// synchronous completion can already expose the final state. Publication precedes task completion.
    /// Expected GitHub and operating-system errors complete normally with failed state; other faults propagate.
    /// Once stopping, this returns a completed task without starting work.
    /// </remarks>
    public Task RefreshAsync()
    {
        TaskCompletionSource completion;
        DashboardSnapshot? previous;
        CancellationToken cancellationToken;
        lock (_gate)
        {
            if (_state.IsStopping)
            {
                return Task.CompletedTask;
            }

            if (_refreshTask is not null)
            {
                return _refreshTask;
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _refreshTask = completion.Task;
            previous = _recoverySnapshot;
            cancellationToken = _lifetime.Token;
            _state = new(_state.Snapshot, _state.LastKnownLogin, _state.Error, true, false);
        }

        _ = RefreshCoreAsync(previous, cancellationToken, completion);
        return completion.Task;
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
        lock (_gate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = completion.Task;
            _state = new(null, _state.LastKnownLogin, null, false, true);
            _recoverySnapshot = null;
            refreshTask = _refreshTask ?? Task.CompletedTask;
        }

        _ = ShutdownCoreAsync(refreshTask, completion);
        return completion.Task;
    }

    /// <summary>Delegates to <see cref="ShutdownAsync"/>; repeated disposal shares the same shutdown completion.</summary>
    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task RefreshCoreAsync(
        DashboardSnapshot? previous,
        CancellationToken cancellationToken,
        TaskCompletionSource completion)
    {
        DashboardSnapshot? snapshot = null;
        string? error = null;
        Exception? unexpectedFault = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refreshed = await _service.RefreshAsync(previous, cancellationToken).ConfigureAwait(false);
            snapshot = FreezeSnapshot(refreshed);
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

        lock (_gate)
        {
            if (!_state.IsStopping)
            {
                if (snapshot is not null)
                {
                    _recoverySnapshot = snapshot;
                }

                _state = new(snapshot, snapshot?.User.Login ?? _state.LastKnownLogin, error, false, false);
            }

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
        }
    }

    private async Task ShutdownCoreAsync(Task refreshTask, TaskCompletionSource completion)
    {
        try
        {
            using (_lifetime)
            {
                // Drain the refresh even if a cancellation callback faults; never run callbacks under the gate.
                await Task.WhenAll(_lifetime.CancelAsync(), refreshTask).ConfigureAwait(false);
            }

            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private static DashboardSnapshot FreezeSnapshot(DashboardSnapshot snapshot) =>
        snapshot with
        {
            Activity = FreezeSection(snapshot.Activity),
            PullRequests = FreezeSection(snapshot.PullRequests),
            ReviewRequests = FreezeSection(snapshot.ReviewRequests),
            Repositories = FreezeSection(snapshot.Repositories),
            Contributions = snapshot.Contributions with
            {
                Calendar = snapshot.Contributions.Calendar is { } calendar
                    ? calendar with
                    {
                        Weeks = calendar.Weeks.Select(week => week with
                        {
                            Days = week.Days.ToImmutableArray()
                        }).ToImmutableArray()
                    }
                    : null
            }
        };

    private static DashboardSection FreezeSection(DashboardSection section) =>
        section with { Items = section.Items.ToImmutableArray() };
}
