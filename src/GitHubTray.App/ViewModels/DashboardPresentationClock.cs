namespace GitHubTray_App.ViewModels;

/// <summary>Visibility lifecycle for the local presentation clock, never the API refresh timer.</summary>
internal sealed class DashboardPresentationClock(Action start, Action stop, Action update)
{
    private bool _isInitialized;
    private bool _isVisible;
    private bool _isStopped;

    private bool IsRunning => _isInitialized && _isVisible && !_isStopped;

    public void Initialize()
    {
        if (_isInitialized || _isStopped)
            return;
        _isInitialized = true;
        if (_isVisible)
        {
            // Startup data can already be visible while settings are still loading.
            update();
            start();
        }
    }

    public void SetVisible(bool isVisible)
    {
        if (_isStopped || _isVisible == isVisible)
            return;

        var wasRunning = IsRunning;
        _isVisible = isVisible;
        if (IsRunning)
        {
            // Catch up before the native window is shown, not at the next minute tick.
            update();
            start();
        }
        else if (wasRunning)
        {
            stop();
        }
    }

    public void Tick()
    {
        // A queued tick can still arrive after the timer was stopped.
        if (IsRunning)
            update();
    }

    public void Stop()
    {
        var wasRunning = IsRunning;
        _isStopped = true;
        if (wasRunning)
            stop();
    }
}
