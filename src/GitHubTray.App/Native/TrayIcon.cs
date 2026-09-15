using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace GitHubTray_App.Native;

internal enum TrayCommand
{
    Open = 101,
    Refresh = 102,
    Settings = 103,
    Quit = 104
}

/// <summary>
/// Owns one top-level hidden Win32 callback window (not message-only: it must receive
/// Explorer's TaskbarCreated broadcast), its rooted delegate, and the shell icon.
/// Created, used, and disposed on the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private static readonly Guid IconGuid = new("a69b868c-c354-41c3-99c2-1cbd701c84af");
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _restoreTimer;
    private readonly NativeMethods.WindowProcedure _windowProcedure;
    private readonly string _className = $"GitHubTray.NotificationArea.{Environment.ProcessId}";
    private readonly nint _instance;
    private readonly uint _taskbarCreated;
    private nint _window;
    private nint _icon;
    private bool _isClassRegistered;
    private bool _isDisposed;
    private int _restoreAttempts;

    public TrayIcon(DispatcherQueue dispatcher, string iconPath)
    {
        _dispatcher = dispatcher;
        _windowProcedure = WindowProcedure;
        _instance = NativeMethods.GetModuleHandle(null);
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _restoreTimer = dispatcher.CreateTimer();
        _restoreTimer.Interval = TimeSpan.FromSeconds(2);
        _restoreTimer.Tick += RestoreTimer_Tick;

        try
        {
            if (_taskbarCreated == 0)
            {
                throw new Win32Exception("Explorer notification registration failed.");
            }

            var windowClass = new NativeMethods.WindowClass
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                Instance = _instance,
                ClassName = _className
            };
            _isClassRegistered = NativeMethods.RegisterClassEx(ref windowClass) != 0;
            if (!_isClassRegistered)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _window = NativeMethods.CreateWindowEx(0x80, _className, "GitHub Tray notifications",
                0, 0, 0, 0, 0, 0, 0, _instance, 0);
            if (_window == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _icon = NativeMethods.LoadImage(0, iconPath, 1, 32, 32, 0x10);
            if (_icon == 0)
            {
                throw new Win32Exception("The notification icon could not be loaded.");
            }

            if (!TryAddIcon())
            {
                throw new Win32Exception("Windows did not accept the notification icon.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsAvailable { get; private set; }
    public event Action? ToggleRequested;
    public event Action<TrayCommand>? CommandRequested;
    public event Action<string?>? AvailabilityChanged;

    public void UpdateIcon(string iconPath)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var newIcon = NativeMethods.LoadImage(0, iconPath, 1, 32, 32, 0x10);
        if (newIcon == 0)
        {
            throw new Win32Exception("The notification icon could not be loaded.");
        }

        if (IsAvailable)
        {
            var data = CreateData();
            data.Icon = newIcon;
            if (!NativeMethods.Shell_NotifyIcon(1, ref data))
            {
                NativeMethods.DestroyIcon(newIcon);
                throw new Win32Exception("Windows did not accept the updated notification icon.");
            }
        }

        var oldIcon = _icon;
        _icon = newIcon;
        if (oldIcon != 0)
        {
            NativeMethods.DestroyIcon(oldIcon);
        }
    }

    private NativeMethods.NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Window = _window,
        Id = IconId,
        // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP
        Flags = 0x1 | 0x2 | 0x4 | 0x20 | 0x80,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "GitHub Tray",
        Info = "",
        InfoTitle = "",
        Guid = IconGuid
    };

    private bool TryAddIcon()
    {
        if (IsAvailable)
        {
            return true;
        }

        var data = CreateData();
        if (!NativeMethods.Shell_NotifyIcon(0, ref data))
        {
            IsAvailable = false;
            return false;
        }

        data.Version = 4;
        if (!NativeMethods.Shell_NotifyIcon(4, ref data))
        {
            NativeMethods.Shell_NotifyIcon(2, ref data);
            IsAvailable = false;
            return false;
        }

        IsAvailable = true;
        return true;
    }

    public bool TryGetIconRectangle(out NativeMethods.Rect rectangle)
    {
        rectangle = default;
        if (!IsAvailable || _isDisposed)
        {
            return false;
        }

        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>(),
            Window = _window,
            Id = IconId,
            Guid = IconGuid
        };
        return NativeMethods.Shell_NotifyIconGetRect(ref identifier, out rectangle) == 0
               && rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top;
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (_isDisposed)
            {
                return NativeMethods.DefWindowProc(window, message, wParam, lParam);
            }

            if (message == _taskbarCreated)
            {
                // The old icon no longer exists in the new Explorer process.
                IsAvailable = false;
                _restoreAttempts = 0;
                Queue(RestoreAfterExplorerRestart);
                return 0;
            }

            if (message == CallbackMessage)
            {
                var notification = (uint)(lParam.ToInt64() & 0xffff);
                if (notification is 0x400 or 0x401) // NIN_SELECT / NIN_KEYSELECT
                {
                    Queue(() => ToggleRequested?.Invoke());
                }
                else if (notification == 0x7b) // WM_CONTEXTMENU, including keyboard invocation
                {
                    ShowContextMenu(wParam);
                }

                return 0;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or COMException or InvalidOperationException)
        {
            Queue(() => AvailabilityChanged?.Invoke("The notification-area action failed. Keep this panel open and choose Retry tray."));
        }

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private void Queue(Action action)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                action();
            }
        });
    }

    private void RestoreAfterExplorerRestart()
    {
        if (TryAddIcon())
        {
            _restoreTimer.Stop();
            AvailabilityChanged?.Invoke(null);
            return;
        }

        AvailabilityChanged?.Invoke("Explorer restarted and the notification icon is not available yet. This panel will remain accessible; automatic recovery is retrying.");
        _restoreTimer.Start();
    }

    private void RestoreTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (TryAddIcon())
        {
            _restoreTimer.Stop();
            AvailabilityChanged?.Invoke(null);
        }
        else if (++_restoreAttempts >= 15)
        {
            _restoreTimer.Stop();
            AvailabilityChanged?.Invoke("The notification icon could not be restored. Keep this panel open and choose Retry tray.");
        }
    }

    private void ShowContextMenu(nuint packedPoint)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0)
        {
            throw new Win32Exception("The notification-area menu could not be created.");
        }

        try
        {
            if (!NativeMethods.AppendMenu(menu, 0, (nuint)TrayCommand.Open, "&Open")
                || !NativeMethods.AppendMenu(menu, 0, (nuint)TrayCommand.Refresh, "&Refresh")
                || !NativeMethods.AppendMenu(menu, 0, (nuint)TrayCommand.Settings, "&Settings")
                || !NativeMethods.AppendMenu(menu, 0x800, 0, null)
                || !NativeMethods.AppendMenu(menu, 0, (nuint)TrayCommand.Quit, "&Quit"))
            {
                throw new Win32Exception("The notification-area menu could not be populated.");
            }

            var x = unchecked((short)((ulong)packedPoint & 0xffff));
            var y = unchecked((short)(((ulong)packedPoint >> 16) & 0xffff));
            if (x == -1 && y == -1 && TryGetIconRectangle(out var iconRectangle))
            {
                x = (short)((iconRectangle.Left + iconRectangle.Right) / 2);
                y = (short)((iconRectangle.Top + iconRectangle.Bottom) / 2);
            }

            NativeMethods.SetForegroundWindow(_window);
            // TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON; the standard Win32 menu
            // supplies keyboard navigation, accessible item names, and system colors.
            var command = NativeMethods.TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x2, x, y, _window, 0);
            NativeMethods.PostMessage(_window, 0, 0, 0);
            if (Enum.IsDefined(typeof(TrayCommand), (int)command))
            {
                Queue(() => CommandRequested?.Invoke((TrayCommand)command));
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _restoreTimer.Stop();
        _restoreTimer.Tick -= RestoreTimer_Tick;
        if (_window != 0)
        {
            var data = CreateData();
            NativeMethods.Shell_NotifyIcon(2, ref data);
        }

        IsAvailable = false;
        if (_icon != 0)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = 0;
        }

        if (_window != 0)
        {
            NativeMethods.DestroyWindow(_window);
            _window = 0;
        }

        if (_isClassRegistered)
        {
            NativeMethods.UnregisterClass(_className, _instance);
            _isClassRegistered = false;
        }

        GC.KeepAlive(_windowProcedure);
    }
}
