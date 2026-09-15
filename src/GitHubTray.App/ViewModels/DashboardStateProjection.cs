namespace GitHubTray_App.ViewModels;

/// <summary>
/// Projects only the latest immutable state while the panel is visible.
/// Hidden publication still advances the latest state without touching WinUI bindings.
/// </summary>
internal sealed class DashboardStateProjection<TState>(Action<TState> project)
    where TState : class
{
    private TState? _latest;
    private TState? _projected;
    private bool _isVisible;
    private bool _isStopped;

    public void Publish(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_isStopped)
        {
            return;
        }

        _latest = state;
        ProjectLatest();
    }

    public void Hide()
    {
        if (_isStopped)
        {
            return;
        }

        _isVisible = false;
        // Do not keep the previously rendered immutable state alive beside a
        // newer hidden state. Reveal deliberately reprojects authoritative state.
        _projected = null;
    }

    public void Reveal(Func<TState> getCurrentState)
    {
        ArgumentNullException.ThrowIfNull(getCurrentState);
        if (_isStopped)
        {
            return;
        }

        // Read the authoritative owner before making its previous presentation
        // visible. A completion continuation may still be queued on the UI thread.
        var current = getCurrentState();
        ArgumentNullException.ThrowIfNull(current);
        _latest = current;
        _isVisible = true;
        ProjectLatest();
    }

    public void Stop(TState terminalState)
    {
        ArgumentNullException.ThrowIfNull(terminalState);
        if (_isStopped)
        {
            return;
        }

        _latest = terminalState;
        if (!ReferenceEquals(_projected, terminalState))
        {
            project(terminalState);
            _projected = terminalState;
        }

        _isVisible = false;
        _isStopped = true;
    }

    private void ProjectLatest()
    {
        if (!_isVisible || _latest is null || ReferenceEquals(_projected, _latest))
        {
            return;
        }

        project(_latest);
        _projected = _latest;
    }
}
