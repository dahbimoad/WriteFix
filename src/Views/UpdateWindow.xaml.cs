using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using WriteFix.Services.Logging;
using WriteFix.Services.Updates;

namespace WriteFix.Views;

/// <summary>
/// Asks whether to install a newer WriteFix, then does it. The whole update
/// lives in this one window: the question, the download, and the handover to
/// setup. Nothing is fetched until the user presses Update now.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updates;
    private readonly ReleaseInfo _release;

    /// <summary>
    /// Starts the downloaded installer and shuts WriteFix down, because setup
    /// cannot replace files this process is holding open. False means it could
    /// not be started, and the window stays up to say so.
    /// </summary>
    private readonly Func<string, bool> _handOver;

    private readonly bool _canInstall;

    private CancellationTokenSource? _download;
    private bool _downloading;

    public UpdateWindow(UpdateService updates, ReleaseInfo release, Func<string, bool> handOver)
    {
        InitializeComponent();

        _updates = updates;
        _release = release;
        _handOver = handOver;
        _canInstall = release.HasInstaller && UpdateService.InstalledBySetup();

        HeadlineText.Text = $"WriteFix {release.Version} is available";
        SubheadText.Text = $"You have {UpdateService.CurrentVersion}.";
        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "No release notes were published."
            : release.Notes;

        if (_canInstall) return;

        UpdateButton.Content = "Open download page";
        ShowStatus("This copy was not installed by the setup program, so the download page opens instead.");
    }

    /// <summary>True when the user asked not to be told about this version again.</summary>
    public bool VersionSkipped { get; private set; }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (!_canInstall)
        {
            OpenReleasePage();
            return;
        }

        BeginDownload();

        try
        {
            var installer = await _updates.DownloadAsync(
                _release,
                new Progress<DownloadProgress>(ShowProgress),
                _download!.Token);

            ShowStatus("Starting the installer. WriteFix will close and come back.");

            if (!_handOver(installer))
                EndDownload("The installer could not be started. Try the download page instead.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Update download cancelled.");
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Update download failed.", ex);
            EndDownload($"The download failed: {ex.Message}");
        }
    }

    /// <summary>Later, or Cancel once a download is running. Same button, one job at a time.</summary>
    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            _download?.Cancel();
            LaterButton.IsEnabled = false;
            ShowStatus("Cancelling...");
            return;
        }

        VersionSkipped = SkipBox.IsChecked == true;
        Close();
    }

    private void OpenReleasePage()
    {
        Process.Start(new ProcessStartInfo(_release.PageUrl) { UseShellExecute = true });
        Close();
    }

    private void BeginDownload()
    {
        _download = new CancellationTokenSource();
        _downloading = true;

        SkipBox.IsChecked = false;
        SkipBox.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        LaterButton.Content = "Cancel";

        DownloadBar.Value = 0;
        DownloadBar.Visibility = Visibility.Visible;
        ShowStatus("Downloading the installer...");
    }

    /// <summary>Back to the question, so a failed download can simply be retried.</summary>
    private void EndDownload(string message)
    {
        _download?.Dispose();
        _download = null;
        _downloading = false;

        DownloadBar.Visibility = Visibility.Collapsed;
        DownloadBar.IsIndeterminate = false;
        SkipBox.IsEnabled = true;
        UpdateButton.IsEnabled = true;
        LaterButton.Content = "Later";
        LaterButton.IsEnabled = true;
        ShowStatus(message);
    }

    private void ShowProgress(DownloadProgress progress)
    {
        var done = progress.BytesDone / 1024d / 1024d;

        if (progress.BytesTotal <= 0)
        {
            // No Content-Length: show motion rather than a bar stuck at zero.
            DownloadBar.IsIndeterminate = true;
            ShowStatus($"Downloading... {done:0.0} MB");
            return;
        }

        DownloadBar.Value = 1000d * progress.BytesDone / progress.BytesTotal;
        ShowStatus($"Downloading... {done:0.0} MB of {progress.BytesTotal / 1024d / 1024d:0.0} MB");
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _download?.Dispose();
        base.OnClosed(e);
    }
}
