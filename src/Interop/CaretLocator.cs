using WriteFix.Services.Logging;
using static WriteFix.Interop.NativeMethods;

namespace WriteFix.Interop;

/// <summary>
/// Finds a sensible screen point to anchor the result card to: the text caret of
/// the app being written in, or the mouse pointer when there is no caret.
/// Coordinates are physical pixels.
/// </summary>
internal static class CaretLocator
{
    public static (int X, int Y) GetAnchorPoint()
    {
        if (TryGetCaret(out var caret, out var reason))
        {
            AppLog.Info($"Card anchor: caret at {caret.X},{caret.Y}.");
            return caret;
        }

        if (GetCursorPos(out var cursor))
        {
            AppLog.Info($"Card anchor: mouse at {cursor.X},{cursor.Y}. noCaret={reason}");
            return (cursor.X, cursor.Y);
        }

        AppLog.Warn($"Card anchor: neither caret nor mouse available; using 0,0. noCaret={reason}");
        return (0, 0);
    }

    private static bool TryGetCaret(out (int X, int Y) point, out string reason)
    {
        point = default;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            reason = "no-foreground-window";
            return false;
        }

        var thread = GetWindowThreadProcessId(foreground, out _);
        if (thread == 0)
        {
            reason = "no-window-thread";
            return false;
        }

        var info = new GUITHREADINFO();
        info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>();

        if (!GetGUIThreadInfo(thread, ref info))
        {
            reason = "GetGUIThreadInfo-failed";
            return false;
        }

        if (info.hwndCaret == IntPtr.Zero)
        {
            reason = "no-caret-window";
            return false;
        }

        // A zero-area caret rect means the provider does not really report one.
        var height = info.rcCaret.Bottom - info.rcCaret.Top;
        if (height <= 0)
        {
            reason = "zero-height-caret";
            return false;
        }

        var client = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
        if (!ClientToScreen(info.hwndCaret, ref client))
        {
            reason = "ClientToScreen-failed";
            return false;
        }

        point = (client.X, client.Y);
        reason = "";
        return true;
    }
}
