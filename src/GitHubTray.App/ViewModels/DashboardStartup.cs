using GitHubTray.AppState;
using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

/// <summary>
/// Owns the initial flight before a window exists, then hands that same flight to
/// the dashboard. Settings and UI construction are not prerequisites for fetching.
/// </summary>
internal sealed class DashboardStartup : IAsyncDisposable
{
    private Task? _initializeTask;

    private DashboardStartup(DashboardRefreshSession session)
    {
        Session = session;
        RefreshTask = session.RefreshAsync(DashboardRefreshReason.Startup);
    }

    public DashboardRefreshSession Session { get; }
    public Task RefreshTask { get; }

    // Keep session/service creation inside this gate: secondary launches only redirect.
    public static DashboardStartup? StartIfPrimary(
        bool isPrimary, Func<DashboardRefreshSession> createSession) =>
        isPrimary ? new DashboardStartup(createSession()) : null;

    public async Task<TWindow> CreateWindowAsync<TWindow>(Func<DashboardStartup, TWindow> createWindow)
    {
        try
        {
            return createWindow(this);
        }
        catch
        {
            // A failed constructor cannot take ownership of the accepted flight.
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>Called on the UI thread; state projection resumes on that same context.</summary>
    public Task InitializeAsync(
        Func<Task> initializeSettingsAsync, Action<DashboardSessionState> projectState) =>
        _initializeTask ??= InitializeCoreAsync(initializeSettingsAsync, projectState);

    private async Task InitializeCoreAsync(
        Func<Task> initializeSettingsAsync, Action<DashboardSessionState> projectState)
    {
        // Observe first, even when settings are slow or the initial flight already completed.
        var projection = ObserveRefreshAsync(projectState);
        Task settings;
        try
        {
            settings = initializeSettingsAsync();
        }
        catch (Exception exception)
        {
            settings = Task.FromException(exception);
        }
        await Task.WhenAll(projection, settings);
    }

    private async Task ObserveRefreshAsync(Action<DashboardSessionState> projectState)
    {
        var projected = Session.State;
        projectState(projected);
        try
        {
            await Session.InitialHydrationTask;
            var hydrated = Session.State;
            if (!ReferenceEquals(projected, hydrated))
            {
                projectState(hydrated);
                projected = hydrated;
            }
            await RefreshTask;
        }
        finally
        {
            var completed = Session.State;
            if (!ReferenceEquals(projected, completed))
            {
                projectState(completed);
            }
        }
    }

    public ValueTask DisposeAsync() =>
        // Also observe an already-retired initial fault; shutdown alone need not retain it.
        new(Task.WhenAll(Session.ShutdownAsync(), RefreshTask));
}
