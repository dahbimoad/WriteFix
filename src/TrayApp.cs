using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using WriteFix.Interop;
using WriteFix.Models;
using WriteFix.Services.Ai;
using WriteFix.Services.Capture;
using WriteFix.Services.Correction;
using WriteFix.Services.Logging;
using WriteFix.Services.Settings;
using WriteFix.Services.Updates;
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
    private readonly AiRouter _ai;
    private readonly AppMessageWindow _messages;
    private readonly CorrectionCoordinator _coordinator;
    private readonly UpdateCoordinator _updates;

    private SettingsWindow? _settingsWindow;

    public TrayApp()
    {
        _settings = new SettingsStore();
        _settings.Load();

        _secrets = new SecretStore();
        _ai = new AiRouter(_settings, _secrets);
        _coordinator = new CorrectionCoordinator(new TextCaptureService(), _ai, _settings, Notify);
        _updates = new UpdateCoordinator(_settings, () => _settingsWindow);

        _messages = new AppMessageWindow();
        _messages.HotkeyPressed += _coordinator.Run;
        _messages.ShowSettingsRequested += OpenSettings;

        _icon = BuildTrayIcon();

        RegisterHotkeys();

        // Opted-in users only, once a day. Everyone else hears nothing until they
        // press Check for updates themselves.
        _updates.CheckInBackground();
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Fix now", null, (_, _) => _coordinator.Run(CorrectionMode.Fix));
        menu.Items.Add("Rephrase now", null, (_, _) => _coordinator.Run(CorrectionMode.Rephrase));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Check for updates…", null, (_, _) => CheckForUpdates());
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

    /// <summary>
    /// The tray route to an update check. The window speaks for itself when there
    /// is one; a balloon is the only way to say "nothing new" from here.
    /// </summary>
    private async void CheckForUpdates()
    {
        Notify("Checking for updates…");

        try
        {
            var result = await _updates.CheckAsync(manual: true);
            if (result.Release is null) Notify(result.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("Update check failed.", ex);
            Notify("The update check failed. WriteFix is still running.");
        }
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

        _settingsWindow = new SettingsWindow(_settings, _secrets, _ai, _updates, _messages);
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

    private void RegisterHotkeys()
    {
        var taken = _messages.RegisterSavedHotkeys(_settings.Current);

        if (taken.Count > 0)
            Notify($"{string.Join(" and ", taken)} {(taken.Count == 1 ? "is" : "are")} already used by another app. " +
                   "Choose a different shortcut in Settings.");
    }

    private void Notify(string message)
    {
        // Balloon text is user-facing only; nothing here reaches the log.
        _icon.ShowBalloonTip(3500, "WriteFix", message, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _messages.Dispose();
        _ai.Dispose();
        _updates.Dispose();

        _icon.Visible = false;
        _icon.Dispose();
    }
}
