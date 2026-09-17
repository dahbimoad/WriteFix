using System.Windows.Interop;
using WriteFix.Models;
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

    /// <summary>Base id; each <see cref="CorrectionMode"/> registers at its own offset from it.</summary>
    private const int HotkeyIdBase = 0xA17F;

    /// <summary>Borrowed for an instant to test whether a combination is free.</summary>
    private const int ProbeHotkeyId = 0xA1FF;

    private const int WS_POPUP = unchecked((int)0x80000000);

    private readonly HwndSource _source;
    private readonly uint _showSettingsMessage;
    private readonly Dictionary<CorrectionMode, HotkeySpec> _registered = [];

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

    /// <summary>Raised on the UI thread with the mode whose hotkey the user pressed.</summary>
    public event Action<CorrectionMode>? HotkeyPressed;

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
    /// Registers every mode's saved hotkey, replacing all previous registrations.
    /// Returns the combinations another application already owns (empty when all worked).
    /// </summary>
    public IReadOnlyList<HotkeySpec> RegisterSavedHotkeys(AppSettings settings)
    {
        UnregisterHotkeys();

        var taken = new List<HotkeySpec>();

        foreach (var mode in Enum.GetValues<CorrectionMode>())
        {
            var spec = HotkeySpec.SavedFor(settings, mode);
            if (!RegisterHotkey(mode, spec)) taken.Add(spec);
        }

        return taken;
    }

    /// <summary>
    /// Releases every WriteFix hotkey. Settings does this while a shortcut is being
    /// typed, so pressing the current combination records it instead of starting a correction.
    /// </summary>
    public void UnregisterHotkeys()
    {
        foreach (var mode in _registered.Keys)
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyIdFor(mode));

        _registered.Clear();
    }

    /// <summary>
    /// True when no other application holds <paramref name="spec"/> system-wide. A
    /// combination WriteFix itself holds counts as available: saving reassigns it.
    /// Shortcuts an app only handles inside its own window cannot be detected this way.
    /// </summary>
    public bool IsHotkeyAvailable(HotkeySpec spec)
    {
        if (_registered.ContainsValue(spec)) return true;

        if (!NativeMethods.RegisterHotKey(_source.Handle, ProbeHotkeyId, (uint)spec.Modifiers, spec.VirtualKey))
            return false;

        NativeMethods.UnregisterHotKey(_source.Handle, ProbeHotkeyId);
        return true;
    }

    private bool RegisterHotkey(CorrectionMode mode, HotkeySpec spec)
    {
        // NoRepeat stops a held-down hotkey from queuing a burst of corrections.
        var modifiers = (uint)(spec.Modifiers | HotkeyModifiers.NoRepeat);

        if (!NativeMethods.RegisterHotKey(_source.Handle, HotkeyIdFor(mode), modifiers, spec.VirtualKey))
        {
            AppLog.Warn($"{mode} hotkey {spec} is already taken by another application.");
            return false;
        }

        _registered[mode] = spec;
        AppLog.Info($"{mode} hotkey registered: {spec}");
        return true;
    }

    private static int HotkeyIdFor(CorrectionMode mode) => HotkeyIdBase + (int)mode;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var mode = (CorrectionMode)(wParam.ToInt32() - HotkeyIdBase);

            if (_registered.ContainsKey(mode))
            {
                handled = true;
                HotkeyPressed?.Invoke(mode);
            }

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
        UnregisterHotkeys();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
