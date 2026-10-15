using System.Diagnostics;
using WriteFix.Interop;
using WriteFix.Models;
using WriteFix.Services.Ai;
using WriteFix.Services.Capture;
using WriteFix.Services.Logging;
using WriteFix.Services.Platform;
using WriteFix.Services.Settings;
using WriteFix.Views;

namespace WriteFix.Services.Correction;

/// <summary>
/// Drives one correction from hotkey to replacement: capture → ask the AI → show the
/// card → act on what the user chooses. Only one runs at a time; pressing the hotkey
/// again cancels the previous one (ARCHITECTURE.md §4).
/// </summary>
public sealed class CorrectionCoordinator
{
    private readonly TextCaptureService _capture;
    private readonly AiRouter _ai;
    private readonly SettingsStore _settings;
    private readonly Action<string> _notify;

    private CancellationTokenSource? _cancellation;
    private ResultWindow? _window;

    /// <summary>The correction currently shown on the card.</summary>
    private string _corrected = "";

    /// <summary>The mode of the request currently shown or in flight.</summary>
    private CorrectionMode _mode;

    public CorrectionCoordinator(
        TextCaptureService capture,
        AiRouter ai,
        SettingsStore settings,
        Action<string> notify)
    {
        _capture = capture;
        _ai = ai;
        _settings = settings;
        _notify = notify;
    }

    /// <param name="requested">The mode the shortcut or menu asked for; a mode locked in Settings wins over it.</param>
    public async void Run(CorrectionMode requested)
    {
        try
        {
            await RunAsync(requested);
        }
        catch (Exception ex)
        {
            // Last line of defence: an operation must never take the tray process down.
            AppLog.Error("Correction operation failed.", ex);
            Dismiss();
            _notify("Something went wrong. WriteFix is still running.");
        }
    }

    private async Task RunAsync(CorrectionMode requested)
    {
        Dismiss();

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        var lockedMode = _settings.Current.LockedMode;
        _mode = lockedMode ?? requested;

        AppLog.Info($"Correction started. requested={requested} locked={lockedMode?.ToString() ?? "none"} mode={_mode}");

        // Read the caret before our own window can become the foreground window.
        var anchor = CaretLocator.GetAnchorPoint();

        var stopwatch = Stopwatch.StartNew();
        var captured = await StaRunner.RunAsync(() => _capture.Capture(_settings.Current.MaxInputCharacters));
        AppLog.Info($"Capture returned. status={captured.Status} ms={stopwatch.ElapsedMilliseconds}");

        if (captured.Status != CaptureStatus.Ok)
        {
            _notify(captured.Message);
            return;
        }

        if (cancellation.IsCancellationRequested)
        {
            AppLog.Info("Correction superseded while capturing; card not shown.");
            return;
        }

        var window = new ResultWindow();
        _window = window;

        window.SetAnchor(anchor.X, anchor.Y);
        window.SetModeSwitchVisible(lockedMode is null);
        window.ShowWorking(captured.Mode, _mode);

        window.AcceptRequested += () => OnAccept(captured, window);
        window.CopyRequested += () => OnCopy(window);
        window.RegenerateRequested += () =>
        {
            AppLog.Info($"Regenerate requested. mode={_mode}");
            Rerun(captured, window, _mode);
        };
        window.ModeSwitchRequested += mode =>
        {
            AppLog.Info($"Mode switch requested. from={_mode} to={mode}");
            if (mode != _mode) Rerun(captured, window, mode);
        };
        window.Cancelled += () =>
        {
            AppLog.Info("Correction cancelled by the user.");
            cancellation.Cancel();
            window.CloseQuietly();
        };

        window.Show();
        AppLog.Info($"Card shown. captureMode={captured.Mode} method={captured.Method} canReplace={captured.CanReplaceAutomatically}");

        await RequestCorrectionAsync(captured, window, cancellation.Token);
    }

    private async Task RequestCorrectionAsync(CaptureResult captured, ResultWindow window, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await _ai.CorrectAsync(captured.Text, _mode, token);

        if (token.IsCancellationRequested || !window.IsLoaded)
        {
            AppLog.Info($"AI answer discarded: card already gone. cancelled={token.IsCancellationRequested} ms={stopwatch.ElapsedMilliseconds}");
            return;
        }

        if (!result.Success)
        {
            AppLog.Warn($"AI answer is an error; shown on the card. mode={_mode} ms={stopwatch.ElapsedMilliseconds}");
            window.ShowError(result.ErrorMessage);
            return;
        }

        _corrected = result.Text;

        var copyOnly = !captured.CanReplaceAutomatically;
        var notice = copyOnly
            ? "This message has formatting WriteFix would flatten, so it can only be copied."
            : "";

        AppLog.Info($"AI answer shown. mode={_mode} chars={result.Text.Length} unchanged={result.Text == captured.Text} " +
                    $"copyOnly={copyOnly} ms={stopwatch.ElapsedMilliseconds}");
        window.ShowResult(captured.Text, result.Text, canReplace: !copyOnly, notice);
    }

    private async void OnAccept(CaptureResult captured, ResultWindow window)
    {
        AppLog.Info("Accept pressed.");
        var corrected = _corrected;
        window.CloseQuietly();

        var outcome = await StaRunner.RunAsync(() => _capture.Replace(captured, corrected));

        if (outcome.Replaced)
        {
            AppLog.Info("Accept finished: text replaced in place.");
            return;
        }

        // Could not safely paste — put it on the clipboard so the work is not lost.
        AppLog.Warn("Accept could not replace in place; falling back to the clipboard.");
        await StaRunner.RunAsync(() => ClipboardGuard.SetText(corrected));
        _notify($"{outcome.Message} Copied it to your clipboard instead.");
    }

    private async void OnCopy(ResultWindow window)
    {
        AppLog.Info("Copy pressed.");
        var corrected = _corrected;

        await StaRunner.RunAsync(() => ClipboardGuard.SetText(corrected));
        AppLog.Info("Correction copied to the clipboard.");

        window.FlashCopied();
        await Task.Delay(650);
        window.CloseQuietly();
    }

    /// <summary>Asks again for the same captured text: Regenerate keeps the mode, the card's switch changes it.</summary>
    private async void Rerun(CaptureResult captured, ResultWindow window, CorrectionMode mode)
    {
        var cancellation = _cancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            AppLog.Info("Rerun ignored: the correction is no longer active.");
            return;
        }

        _mode = mode;
        window.ShowWorking(captured.Mode, mode);
        await RequestCorrectionAsync(captured, window, cancellation.Token);
    }

    /// <summary>Cancels any in-flight request and closes the card.</summary>
    public void Dismiss()
    {
        if (_cancellation is not null || _window is not null)
            AppLog.Info($"Dismissing the previous correction. inFlight={_cancellation is not null} cardOpen={_window?.IsLoaded == true}");

        _cancellation?.Cancel();
        _cancellation = null;

        if (_window is not null)
        {
            if (_window.IsLoaded) _window.CloseQuietly();
            _window = null;
        }

        _corrected = "";
    }
}
