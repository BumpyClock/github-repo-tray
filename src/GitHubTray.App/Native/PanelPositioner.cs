using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace GitHubTray_App.Native;

internal static class PanelPositioner
{
    public static void Position(AppWindow appWindow, nint window, TrayIcon? trayIcon)
    {
        NativeMethods.Rect iconRectangle = default;
        var hasIconRectangle = trayIcon?.TryGetIconRectangle(out iconRectangle) == true;
        NativeMethods.GetCursorPos(out var cursor);
        var anchor = hasIconRectangle
            ? new NativeMethods.Point
            {
                X = (iconRectangle.Left + iconRectangle.Right) / 2,
                Y = (iconRectangle.Top + iconRectangle.Bottom) / 2
            }
            : cursor;
        var monitor = NativeMethods.MonitorFromPoint(anchor, 2);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            // DisplayArea is a second, independent source of work-area information.
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var fallback = area.WorkArea;
            monitorInfo.Work = new NativeMethods.Rect
            {
                Left = fallback.X,
                Top = fallback.Y,
                Right = fallback.X + fallback.Width,
                Bottom = fallback.Y + fallback.Height
            };
        }

        var work = monitorInfo.Work;
        // Moving the hidden window to the destination monitor before querying its DPI
        // avoids using the previous monitor's RasterizationScale.
        appWindow.Move(new PointInt32(
            Math.Clamp(anchor.X, work.Left, work.Right - 1),
            Math.Clamp(anchor.Y, work.Top, work.Bottom - 1)));
        var dpi = NativeMethods.GetDpiForWindow(window);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var gap = Math.Max(1, (int)Math.Round(8 * scale));
        var width = Math.Min((int)Math.Round(500 * scale), Math.Max(1, work.Right - work.Left - 2 * gap));
        var height = Math.Min((int)Math.Round(720 * scale), Math.Max(1, work.Bottom - work.Top - 2 * gap));
        var x = anchor.X - width / 2;
        var y = work.Bottom - height - gap;

        if (hasIconRectangle)
        {
            if (iconRectangle.Top >= work.Bottom)
            {
                y = work.Bottom - height - gap;
                x = iconRectangle.Right - width;
            }
            else if (iconRectangle.Bottom <= work.Top)
            {
                y = work.Top + gap;
                x = iconRectangle.Right - width;
            }
            else if (iconRectangle.Right <= work.Left)
            {
                x = work.Left + gap;
                y = iconRectangle.Bottom - height;
            }
            else if (iconRectangle.Left >= work.Right)
            {
                x = work.Right - width - gap;
                y = iconRectangle.Bottom - height;
            }
            else
            {
                // Overflow-area icons can lie inside the work area.
                y = iconRectangle.Top - height - gap;
                if (y < work.Top + gap)
                {
                    y = iconRectangle.Bottom + gap;
                }
            }
        }
        else
        {
            x = work.Right - width - gap;
        }

        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - width - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - height - gap));
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }
}
