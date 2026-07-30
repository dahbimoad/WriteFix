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
        if (TryGetCaret(out var caret)) return caret;

        return GetCursorPos(out var cursor) ? (cursor.X, cursor.Y) : (0, 0);
    }

    private static bool TryGetCaret(out (int X, int Y) point)
    {
        point = default;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        var thread = GetWindowThreadProcessId(foreground, out _);
        if (thread == 0) return false;

        var info = new GUITHREADINFO();
        info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>();

        if (!GetGUIThreadInfo(thread, ref info)) return false;
        if (info.hwndCaret == IntPtr.Zero) return false;

        // A zero-area caret rect means the provider does not really report one.
        var height = info.rcCaret.Bottom - info.rcCaret.Top;
        if (height <= 0) return false;

        var client = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
        if (!ClientToScreen(info.hwndCaret, ref client)) return false;

        point = (client.X, client.Y);
        return true;
    }
}
