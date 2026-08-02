using System.ComponentModel;
using System.IO;
using System.Windows;
using WriteFix.Models;
using WriteFix.Services.Logging;
using WriteFix.Services.Settings;
using WriteFix.Views;
using Application = System.Windows.Application;

namespace WriteFix.Services.Updates;

/// <summary>
/// Drives one update from question to handover: ask GitHub, show the window if
/// there is something newer, and let setup replace this build once the user
/// accepts. Callers decide how to report the outcome, because the same check is
/// started from the Settings footer, from the tray menu, and once a day in the
/// background, and each wants to say it differently.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private readonly UpdateService _updates = new();
    private readonly SettingsStore _settings;

    /// <summary>The Settings window when it is open, which also tells us whether
    /// the user is looking at WriteFix and should get it back after the update.</summary>
    private readonly Func<Window?> _visibleWindow;

    private UpdateWindow? _window;

    public UpdateCoordinator(SettingsStore settings, Func<Window?> visibleWindow)
    {
        _settings = settings;
        _visibleWindow = visibleWindow;
    }

    public bool Busy { get; private set; }

    /// <summary>
    /// The once-a-day check, and only for users who asked for it. Silent unless
    /// there is a release to offer.
    /// </summary>
    public async void CheckInBackground()
    {
        if (!IsAutomaticCheckDue(_settings.Current, DateTimeOffset.UtcNow)) return;

        try
        {
            await CheckAsync(manual: false);
        }
        catch (Exception ex)
        {
            // A background check must never take the tray process down.
            AppLog.Error("Automatic update check failed.", ex);
        }
    }

    /// <summary>
    /// Asks GitHub, and shows the update window when there is something to offer.
    /// A manual check ignores a skipped version, because pressing the button is
    /// the user asking about it again.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(bool manual)
    {
        if (Busy) return UpdateCheck.Failed("A check is already running.");

        Busy = true;
        try
        {
            var result = await _updates.CheckAsync(CancellationToken.None);
            RecordCheckTime();

            if (result.Release is not null && (manual || !IsSkipped(result.Release)))
                Show(result.Release);

            return result;
        }
        finally
        {
            Busy = false;
        }
    }

    private bool IsSkipped(ReleaseInfo release) =>
        string.Equals(_settings.Current.SkippedVersion, release.Version, StringComparison.OrdinalIgnoreCase);

    internal static bool IsAutomaticCheckDue(AppSettings settings, DateTimeOffset now)
    {
        if (!settings.AutoCheckUpdates) return false;
        if (settings.LastUpdateCheck is not { } last) return true;

        // A clock that moved backwards must not park the check forever.
        return last > now || now - last >= CheckEvery;
    }

    private void Show(ReleaseInfo release)
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var owner = _visibleWindow();
        _window = new UpdateWindow(_updates, release, HandOver);

        if (owner is not null)
        {
            _window.Owner = owner;
            _window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        _window.Closed += (_, _) => OnWindowClosed(release);
        _window.Show();
        _window.Activate();
    }

    private void OnWindowClosed(ReleaseInfo release)
    {
        var skipped = _window?.VersionSkipped == true;
        _window = null;

        if (!skipped) return;

        var updated = _settings.Current.Clone();
        updated.SkippedVersion = release.Version;
        _settings.Save(updated);

        AppLog.Info($"Update {release.Version} skipped by the user.");
    }

    /// <summary>
    /// Starts setup and stands down. WriteFix has to be gone before its own files
    /// can be replaced, so this is the last thing the process does.
    /// </summary>
    private bool HandOver(string installerPath)
    {
        var relaunchInBackground = _visibleWindow() is null;

        try
        {
            UpdateService.LaunchInstaller(installerPath, relaunchInBackground);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            AppLog.Error("The downloaded installer could not be started.", ex);
            return false;
        }

        ClearSkippedVersion();
        Application.Current.Shutdown();
        return true;
    }

    private void ClearSkippedVersion()
    {
        if (_settings.Current.SkippedVersion.Length == 0) return;

        var updated = _settings.Current.Clone();
        updated.SkippedVersion = "";
        _settings.Save(updated);
    }

    private void RecordCheckTime()
    {
        var updated = _settings.Current.Clone();
        updated.LastUpdateCheck = DateTimeOffset.UtcNow;
        _settings.Save(updated);
    }

    public void Dispose() => _updates.Dispose();
}
