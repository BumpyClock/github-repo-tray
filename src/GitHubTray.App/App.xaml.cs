using System.Diagnostics;
using GitHubTray.AppState;
using GitHubTray.Core;
using GitHubTray_App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage;

namespace GitHubTray_App;

public partial class App : Application
{
    private AppInstance? _instance;
    private MainWindow? _window;
    private DispatcherQueue? _dispatcher;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => Debug.WriteLine($"Unhandled WinUI exception: {args.Exception}");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Debug.WriteLine("GitHub Tray: resolving the primary instance.");
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _instance = AppInstance.FindOrRegisterForKey("GitHubTray.Primary");
        var startup = DashboardStartup.StartIfPrimary(_instance.IsCurrent,
            static () =>
            {
                var settingsFile = Path.Combine(
                    ApplicationData.Current.LocalFolder.Path, "settings.json");
                var cacheDirectory = Path.Combine(
                    ApplicationData.Current.LocalFolder.Path, "dashboard-cache");
                var service = new DashboardService(
                    new GitHubCliApi(),
                    new JsonDashboardCacheStore(cacheDirectory),
                    reportCacheDiagnostic: diagnostic =>
                        Debug.WriteLine($"GitHub Tray cache: {diagnostic.Kind}: {diagnostic.Message}"));
                return new DashboardRefreshSession(
                    service,
                    startupRefreshInterval: LoadStartupRefreshIntervalAsync(settingsFile));
            });
        if (startup is null)
        {
            // No tray window, timer, or API client is created in secondary instances.
            await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }

        _instance.Activated += Instance_Activated;
        Debug.WriteLine("GitHub Tray: creating the native panel.");
        _window = await startup.CreateWindowAsync(static initial => new MainWindow(initial));
        _window.Closed += Window_Closed;
        _window.ShowPanel();
        Debug.WriteLine("GitHub Tray: loading account data.");
        await _window.ViewModel.InitializeAsync();
    }

    private static async Task<TimeSpan> LoadStartupRefreshIntervalAsync(string settingsFile)
    {
        try
        {
            var settings = await new SettingsStore(settingsFile).LoadAsync().ConfigureAwait(false);
            return TimeSpan.FromMinutes(settings.RefreshMinutes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         System.Text.Json.JsonException or ArgumentException or
                                         System.Security.SecurityException)
        {
            return TimeSpan.FromMinutes(5);
        }
    }

    private void Instance_Activated(object? sender, AppActivationArguments args)
    {
        // AppLifecycle can deliver redirected activations on a non-UI thread.
        _dispatcher?.TryEnqueue(() => _window?.ShowPanel());
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (_instance is not null)
        {
            _instance.Activated -= Instance_Activated;
            _instance.UnregisterKey();
        }
    }
}
