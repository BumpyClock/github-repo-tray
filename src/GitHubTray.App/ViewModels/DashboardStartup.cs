using GitHubTray.AppState;
using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

internal sealed record DashboardStartupResources(
    DashboardRefreshSession Session,
    SettingsStore SettingsStore,
    Task<AppSettings> SettingsTask);

/// <summary>
/// Owns the initial flight before a window exists, then hands that same flight to
/// the dashboard. Settings and UI construction are not prerequisites for fetching.
/// </summary>
internal sealed class DashboardStartup : IAsyncDisposable
{
    private readonly CancellationTokenSource _settingsLifetime;
    private Task? _initializeTask;
    private Task? _disposeTask;

    private DashboardStartup(
        DashboardStartupResources resources,
        CancellationTokenSource settingsLifetime)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _settingsLifetime = settingsLifetime;
        Session = resources.Session;
        SettingsStore = resources.SettingsStore;
        SettingsTask = resources.SettingsTask;
        RefreshTask = Session.RefreshAsync(DashboardRefreshReason.Startup);
    }

    public DashboardRefreshSession Session { get; }
    public SettingsStore SettingsStore { get; }
    public Task<AppSettings> SettingsTask { get; }
    public Task RefreshTask { get; }

    // Keep session/service creation inside this gate: secondary launches only redirect.
    public static DashboardStartup? StartIfPrimary(
        bool isPrimary, Func<CancellationToken, DashboardStartupResources> createResources)
    {
        if (!isPrimary)
        {
            return null;
        }

        var settingsLifetime = new CancellationTokenSource();
        try
        {
            return new DashboardStartup(
                createResources(settingsLifetime.Token),
                settingsLifetime);
        }
        catch
        {
            try
            {
                settingsLifetime.Cancel();
            }
            catch
            {
                // Preserve the resource-construction failure.
            }
            settingsLifetime.Dispose();
            throw;
        }
    }

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
            await Session.InitialVerificationTask;
            var verified = Session.State;
            if (!ReferenceEquals(projected, verified))
            {
                projectState(verified);
                projected = verified;
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

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        // Shutdown is synchronous through terminal-state publication. Observe the
        // shared settings task as well so failed window construction cannot orphan it.
        var shutdown = Session.ShutdownAsync();
        try
        {
            await Task.WhenAll(
                shutdown,
                RefreshTask,
                _settingsLifetime.CancelAsync(),
                ObserveSettingsTaskAsync()).ConfigureAwait(false);
        }
        finally
        {
            _settingsLifetime.Dispose();
        }
    }

    private async Task ObserveSettingsTaskAsync()
    {
        try
        {
            await SettingsTask.ConfigureAwait(false);
        }
        catch
        {
            // The ViewModel owns user-facing settings failures. Disposal only ensures
            // that a task started before window construction is always observed.
        }
    }
}
