namespace GitHubTray_App.ViewModels;

/// <summary>
/// Projects authoritative state while visible and releases presentation when hidden.
/// Hidden publications retain nothing; reveal reads the current state from its owner.
/// </summary>
internal sealed class DashboardStateProjection<TState>(Action<TState> project, Action release)
    where TState : class
{
    private TState? _projected;
    private bool _isVisible;
    private bool _isStopped;

    public void Publish(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_isStopped || !_isVisible)
        {
            return;
        }

        Project(state);
    }

    public void Hide()
    {
        if (_isStopped || !_isVisible)
        {
            return;
        }

        _isVisible = false;
        _projected = null;
        release();
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
        _isVisible = true;
        Project(current);
    }

    public void Stop(TState terminalState)
    {
        ArgumentNullException.ThrowIfNull(terminalState);
        if (_isStopped)
        {
            return;
        }

        Project(terminalState);
        _isVisible = false;
        _isStopped = true;
    }

    private void Project(TState state)
    {
        if (ReferenceEquals(_projected, state))
        {
            return;
        }

        project(state);
        _projected = state;
    }
}
