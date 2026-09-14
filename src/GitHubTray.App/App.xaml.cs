using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

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
        if (!_instance.IsCurrent)
        {
            // No tray window, timer, or API client is created in secondary instances.
            await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }

        _instance.Activated += Instance_Activated;
        Debug.WriteLine("GitHub Tray: creating the native panel.");
        _window = new MainWindow();
        _window.Closed += Window_Closed;
        _window.ShowPanel();
        Debug.WriteLine("GitHub Tray: loading account data.");
        await _window.ViewModel.InitializeAsync();
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
