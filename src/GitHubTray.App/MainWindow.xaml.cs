using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GitHubTray_App.Native;
using GitHubTray_App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;
using Windows.UI.ViewManagement;

namespace GitHubTray_App;

public sealed partial class MainWindow : Window
{
    private const string WindowFrameError = "Windows could not remove the native window outline.";
    private const string LightIconFileName = "AppIcon.ico";
    private const string DarkIconFileName = "AppIconDark.ico";
    private const string HighContrastIconFileName = "AppIconHighContrast.ico";
    private MainPage? _page;
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _systemColors = new();
    private readonly DispatcherQueueTimer _dismissTimer;
    private readonly PanelPerformanceLog? _performanceLog;
    private TrayIcon? _trayIcon;
    private bool _hasAcrylicBackdrop;
    private bool _isQuitting;
    private bool _isActivated;

    internal MainWindow(DashboardStartup startup)
    {
        InitializeComponent();
        ViewModel = new DashboardViewModel(startup, DispatcherQueue);
        ViewModel.SetPanelVisible(false);
        var presenter = AppWindow.Presenter.As<OverlappedPresenter>();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        var cornerPreference = 2; // DWMWCP_ROUND; unsupported systems retain native square corners.
        NativeMethods.DwmSetWindowAttribute(WindowHandle, 33, ref cornerPreference, sizeof(int));
        var borderColor = -2; // DWMWA_COLOR_NONE: keep the native frame borderless.
        NativeMethods.DwmSetWindowAttribute(WindowHandle, 34, ref borderColor, sizeof(int));
        AppWindow.IsShownInSwitchers = false;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        // High contrast toggles do not raise ActualThemeChanged, and
        // AccessibilitySettings.HighContrastChanged requires a UWP window,
        // so the system colour feed is what keeps the icon in step.
        _systemColors.ColorValuesChanged += SystemColors_ColorValuesChanged;
        AppWindow.SetIcon(GetThemeIconPath());
        _dismissTimer = DispatcherQueue.CreateTimer();
        _dismissTimer.Interval = TimeSpan.FromMilliseconds(180);
        _dismissTimer.IsRepeating = false;
        _dismissTimer.Tick += DismissTimer_Tick;
        AppWindow.Closing += AppWindow_Closing;
        Activated += MainWindow_Activated;
        _performanceLog = PanelPerformanceLog.TryCreate(DispatcherQueue);
        RetryTray();
    }

    public DashboardViewModel ViewModel { get; }
    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    private string GetThemeIconPath() => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        GetThemeIconFileName());

    private string GetThemeIconFileName()
    {
        // The high contrast art is a white silhouette, so it only reads against a dark scheme;
        // a light high contrast scheme keeps the dark-glyph icon instead.
        if (_accessibility.HighContrast)
        {
            return IsDarkSystemBackground() ? HighContrastIconFileName : LightIconFileName;
        }

        return RootGrid.ActualTheme == ElementTheme.Dark ? DarkIconFileName : LightIconFileName;
    }

    private bool IsDarkSystemBackground()
    {
        var background = _systemColors.GetColorValue(UIColorType.Background);
        return (background.R * 299 + background.G * 587 + background.B * 114) / 1000 < 128;
    }

    private void ConfigureBackdrop()
    {
        try
        {
            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                _hasAcrylicBackdrop = true;
            }
        }
        catch (Exception exception) when (exception is COMException or NotSupportedException)
        {
            Debug.WriteLine($"Acrylic initialization failed ({exception.HResult:X8}); using the opaque theme surface.");
            SystemBackdrop = null;
            _hasAcrylicBackdrop = false;
        }

        // DesktopAcrylicBackdrop owns system transparency and high-contrast changes.
        // AccessibilitySettings.HighContrastChanged requires a UWP window and fails in WinUI 3.
        FallbackSurface.Visibility = _hasAcrylicBackdrop
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private MainPage EnsurePanelContent()
    {
        if (_page is not null)
        {
            return _page;
        }

        try
        {
            ViewModel.SetPanelVisible(true);
            _page = new MainPage(ViewModel, this);
            PageHost.Child = _page;
            _page.SetPanelVisible(true);
            ConfigureBackdrop();
            return _page;
        }
        catch
        {
            ReleasePanelContent();
            throw;
        }
    }

    private void ReleasePanelContent()
    {
        try
        {
            _page?.ReleaseForHide();
        }
        finally
        {
            PageHost.Child = null;
            _page = null;
            try
            {
                ViewModel.SetPanelVisible(false);
            }
            finally
            {
                SystemBackdrop = null;
                _hasAcrylicBackdrop = false;
                FallbackSurface.Visibility = Visibility.Visible;
            }
        }
    }

    public void ShowPanel(bool showSettings = false)
    {
        var showStarted = _performanceLog is null ? 0 : Stopwatch.GetTimestamp();
        if (_isQuitting)
        {
            return;
        }

        var measureShow = _performanceLog?.BeginShow(showStarted, AppWindow.IsVisible) == true;
        _dismissTimer.Stop();
        // Avoid moving an already visible panel between monitors on reactivation.
        if (!AppWindow.IsVisible)
        {
            PanelPositioner.Position(AppWindow, WindowHandle, _trayIcon);
        }
        var page = EnsurePanelContent();

        if (measureShow)
        {
            _performanceLog!.PreparationComplete();
        }
        AppWindow.Show();
        if (measureShow)
        {
            _performanceLog!.Shown();
        }
        NativeMethods.SetForegroundWindow(WindowHandle);
        Activate();
        try
        {
            WindowFrame.RemoveDialogFrame(WindowHandle);
            if (ViewModel.ActionError == WindowFrameError)
            {
                ViewModel.ActionError = "";
            }
        }
        catch (Win32Exception exception)
        {
            Debug.WriteLine($"Native window frame update failed: {exception}");
            ViewModel.ActionError = WindowFrameError;
        }
        if (showSettings)
        {
            page.OpenSettings();
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isQuitting && AppWindow.IsVisible)
            {
                _page?.FocusPanel();
            }
        });
        if (measureShow)
        {
            _performanceLog!.SynchronousShowComplete();
        }
    }

    public void HidePanel()
    {
        _dismissTimer.Stop();
        if (_isQuitting)
        {
            return;
        }

        if (_trayIcon?.IsAvailable != true)
        {
            ViewModel.TrayError = "The tray icon is unavailable, so the panel cannot be hidden safely. Choose Retry tray, or quit from Settings.";
            AppWindow.IsShownInSwitchers = true;
            return;
        }

        try
        {
            ReleasePanelContent();
        }
        finally
        {
            AppWindow.Hide();
            _performanceLog?.Hidden();
        }
    }

    public void RetryTray()
    {
        if (_isQuitting)
        {
            return;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        try
        {
            _trayIcon = new TrayIcon(DispatcherQueue, GetThemeIconPath());
            _trayIcon.ToggleRequested += TogglePanel;
            _trayIcon.CommandRequested += OnTrayCommandRequested;
            _trayIcon.AvailabilityChanged += OnTrayAvailabilityChanged;
            ViewModel.TrayError = "";
            AppWindow.IsShownInSwitchers = false;
        }
        catch (Exception exception) when (exception is Win32Exception or COMException)
        {
            Debug.WriteLine($"Tray initialization failed ({exception.HResult:X8}).");
            ViewModel.TrayError = "Windows could not create the notification-area icon. Keep this panel open and choose Retry tray. You can quit from Settings.";
            AppWindow.IsShownInSwitchers = true;
        }
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args) => ApplyThemeIcon();

    private void SystemColors_ColorValuesChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isQuitting)
            {
                ApplyThemeIcon();
            }
        });

    private void ApplyThemeIcon()
    {
        var iconPath = GetThemeIconPath();
        AppWindow.SetIcon(iconPath);
        if (_trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.UpdateIcon(iconPath);
        }
        catch (Win32Exception exception)
        {
            Debug.WriteLine($"Tray theme icon update failed ({exception.HResult:X8}).");
            ViewModel.TrayError = "Windows could not update the notification-area icon for the current theme. Choose Retry tray.";
        }
    }

    private void TogglePanel()
    {
        _dismissTimer.Stop();
        if (AppWindow.IsVisible)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    private async void OnTrayCommandRequested(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
                ShowPanel();
                break;
            case TrayCommand.Refresh:
                await ViewModel.RefreshAsync();
                break;
            case TrayCommand.Settings:
                ShowPanel(showSettings: true);
                break;
            case TrayCommand.Quit:
                await QuitAsync();
                break;
        }
    }

    private void OnTrayAvailabilityChanged(string? error)
    {
        ViewModel.TrayError = error ?? "";
        AppWindow.IsShownInSwitchers = error is not null;
        if (error is not null)
        {
            ShowPanel();
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_isQuitting)
        {
            args.Cancel = true;
            HidePanel();
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _isActivated = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!_isActivated && !_isQuitting && _trayIcon?.IsAvailable == true)
        {
            // Defer dismissal so an icon click can toggle the still-visible panel,
            // rather than immediately reopening a panel hidden by deactivation.
            _dismissTimer.Start();
        }
        else
        {
            _dismissTimer.Stop();
        }
    }

    private void DismissTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_isActivated)
        {
            HidePanel();
        }
    }

    public async Task QuitAsync()
    {
        if (_isQuitting)
        {
            return;
        }

        _isQuitting = true;
        _performanceLog?.Dispose();
        _dismissTimer.Stop();
        _dismissTimer.Tick -= DismissTimer_Tick;
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        _systemColors.ColorValuesChanged -= SystemColors_ColorValuesChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
        try
        {
            try
            {
                ReleasePanelContent();
            }
            finally
            {
                await ViewModel.ShutdownAsync();
            }
        }
        finally
        {
            AppWindow.Closing -= AppWindow_Closing;
            Activated -= MainWindow_Activated;
            Close();
            Application.Current.Exit();
        }
    }
}
