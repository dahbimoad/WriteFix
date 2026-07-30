using System.Windows.Interop;
using WriteFix.Services.Logging;

namespace WriteFix.Interop;

/// <summary>
/// One hidden top-level window that receives everything Windows sends WriteFix:
/// the global hotkey, and the "show yourself" broadcast from a second launch.
///
/// It has to be a real top-level window rather than a message-only one — message-only
/// windows do not receive HWND_BROADCAST.
/// </summary>
public sealed class AppMessageWindow : IDisposable
{
    /// <summary>Any WriteFix instance in this session resolves this to the same message id.</summary>
    public const string ShowSettingsMessageName = "WriteFix.ShowSettings.v1";

    private const int HotkeyId = 0xA17F;
    private const int WS_POPUP = unchecked((int)0x80000000);

    private readonly HwndSource _source;
    private readonly uint _showSettingsMessage;
    private bool _registered;

    public AppMessageWindow()
    {
        var parameters = new HwndSourceParameters("WriteFixMessageSink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = WS_POPUP,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _showSettingsMessage = NativeMethods.RegisterWindowMessage(ShowSettingsMessageName);
    }

    /// <summary>Raised on the UI thread when the user presses the configured hotkey.</summary>
    public event Action? HotkeyPressed;

    /// <summary>Raised when another launch of WriteFix asks the running copy to show itself.</summary>
    public event Action? ShowSettingsRequested;

    /// <summary>
    /// Tells an already-running WriteFix to open its Settings window. Called by the
    /// second instance just before it exits.
    /// </summary>
    public static void BroadcastShowSettings()
    {
        var message = NativeMethods.RegisterWindowMessage(ShowSettingsMessageName);
        if (message == 0) return;

        NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, message, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Registers <paramref name="spec"/>, replacing any previous registration.
    /// Returns false when another application already owns the combination.
    /// </summary>
    public bool RegisterHotkey(HotkeySpec spec)
    {
        UnregisterHotkey();

        // NoRepeat stops a held-down hotkey from queuing a burst of corrections.
        var modifiers = (uint)(spec.Modifiers | HotkeyModifiers.NoRepeat);
        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers, spec.VirtualKey);

        if (_registered)
            AppLog.Info($"Hotkey registered: {spec}");
        else
            AppLog.Warn($"Hotkey {spec} is already taken by another application.");

        return _registered;
    }

    public void UnregisterHotkey()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke();
            return IntPtr.Zero;
        }

        if (_showSettingsMessage != 0 && (uint)msg == _showSettingsMessage)
        {
            handled = true;
            AppLog.Info("Second launch asked the running instance to show Settings.");
            ShowSettingsRequested?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotkey();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
