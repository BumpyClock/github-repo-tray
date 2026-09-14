using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GitHubTray_App.Native;

internal static class WindowFrame
{
    private const int WindowStyle = -16;
    private const int ExtendedWindowStyle = -20;
    private const int DialogFrame = 0x00400000;
    private const int RaisedWindowEdge = 0x00000100;
    private const uint RefreshFrameOnly = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020;

    public static void RemoveDialogFrame(nint window)
    {
        // The non-resizable WinUI presenter can retain these despite HasBorder=false.
        var changed = ClearStyle(window, WindowStyle, DialogFrame);
        changed |= ClearStyle(window, ExtendedWindowStyle, RaisedWindowEdge);
        if (changed && !NativeMethods.SetWindowPos(window, 0, 0, 0, 0, 0, RefreshFrameOnly))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not refresh the native window frame.");
        }
    }

    private static bool ClearStyle(nint window, int index, int mask)
    {
        var current = NativeMethods.GetWindowLong(window, index);
        var error = Marshal.GetLastPInvokeError();
        if (current == 0 && error != 0)
        {
            throw new Win32Exception(error, "Could not read the native window style.");
        }

        var updated = current & ~mask;
        if (updated == current)
        {
            return false;
        }
        var previous = NativeMethods.SetWindowLong(window, index, updated);
        error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
        {
            throw new Win32Exception(error, "Could not update the native window style.");
        }
        return true;
    }
}
