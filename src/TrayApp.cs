using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using WriteFix.Interop;
using WriteFix.Services.Ai;
using WriteFix.Services.Capture;
using WriteFix.Services.Correction;
using WriteFix.Services.Logging;
using WriteFix.Services.Settings;
using WriteFix.Views;
using Application = System.Windows.Application;

namespace WriteFix;

/// <summary>
/// The app itself: a tray icon, a global hotkey, and the services they drive.
/// Constructed once in App.xaml.cs — this is the composition root.
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly SettingsStore _settings;
    private readonly SecretStore _secrets;
    private readonly OpenRouterClient _client;
    private readonly AppMessageWindow _messages;
    private readonly CorrectionCoordinator _coordinator;

    private SettingsWindow? _settingsWindow;

    public TrayApp()
    {
        _settings = new SettingsStore();
        _settings.Load();

        _secrets = new SecretStore();
        _client = new OpenRouterClient(_settings, _secrets);
        _coordinator = new CorrectionCoordinator(new TextCaptureService(), _client, _settings, Notify);

        _messages = new AppMessageWindow();
        _messages.HotkeyPressed += _coordinator.Run;
        _messages.ShowSettingsRequested += OpenSettings;

        _icon = BuildTrayIcon();

        ApplyHotkey(HotkeySpec.ParseOrDefault(_settings.Current.Hotkey));
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Correct now", null, (_, _) => _coordinator.Run());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());

        var icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "WriteFix",
            Visible = true,
            ContextMenuStrip = menu,
        };

        icon.DoubleClick += (_, _) => OpenSettings();
        return icon;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/writefix.ico"));
            if (resource is not null) return new Icon(resource.Stream);
        }
        catch (Exception ex)
        {
            AppLog.Error("Tray icon resource could not be loaded.", ex);
        }

        return SystemIcons.Application;
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            BringToFront(_settingsWindow);
            return;
        }

        // The card and Settings competing for focus helps nobody.
        _coordinator.Dismiss();

        _settingsWindow = new SettingsWindow(_settings, _secrets, _client, ApplyHotkey);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        BringToFront(_settingsWindow);
    }

    /// <summary>
    /// Windows refuses a plain Activate() from a background process, which is exactly
    /// what we are when the user re-launches the exe. Un-minimising and briefly
    /// flipping Topmost gets the window in front without a global focus hook.
    /// </summary>
    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Show();
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
        window.Focus();
    }

    /// <summary>Registers <paramref name="spec"/>; false means another app already owns it.</summary>
    private bool ApplyHotkey(HotkeySpec spec)
    {
        var registered = _messages.RegisterHotkey(spec);

        if (!registered)
            Notify($"{spec} is already used by another app. Choose a different hotkey in Settings.");

        return registered;
    }

    private void Notify(string message)
    {
        // Balloon text is user-facing only; nothing here reaches the log.
        _icon.ShowBalloonTip(3500, "WriteFix", message, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _messages.Dispose();
        _client.Dispose();

        _icon.Visible = false;
        _icon.Dispose();
    }
}
