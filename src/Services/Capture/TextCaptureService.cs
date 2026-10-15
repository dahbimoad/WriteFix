using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using WriteFix.Interop;
using WriteFix.Models;
using WriteFix.Services.Logging;
using static WriteFix.Interop.NativeMethods;

namespace WriteFix.Services.Capture;

/// <summary>
/// Reads text out of whatever field the user is writing in, and puts corrected text
/// back. UI Automation first; a guarded clipboard round-trip only when UIA has
/// already proved the control is a writable, non-password text field.
///
/// All members must be called from an STA thread and never from the WPF UI thread —
/// cross-process UIA calls can block (ARCHITECTURE.md §5).
/// </summary>
public sealed class TextCaptureService
{
    private const int ClipboardSettleMs = 140;
    private const int PasteSettleMs = 260;
    private const int FocusTimelineMs = 1000;
    private const int FocusTimelineStepMs = 25;

    public CaptureResult Capture(int maxCharacters)
    {
        try
        {
            return CaptureCore(maxCharacters);
        }
        catch (ElementNotAvailableException)
        {
            AppLog.Warn("Capture failed: the focused element disappeared.");
            return CaptureResult.Fail(CaptureStatus.Failed, "The window closed before WriteFix could read it.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture failed.", ex);
            return CaptureResult.Fail(CaptureStatus.Failed, "WriteFix could not read this field.");
        }
    }

    private CaptureResult CaptureCore(int maxCharacters)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            AppLog.Warn("Capture refused: no foreground window.");
            return CaptureResult.Fail(CaptureStatus.Failed, "No active window.");
        }

        var element = AutomationElement.FocusedElement;
        if (element is null)
        {
            AppLog.Warn("Capture refused: UI Automation reports no focused element.");
            return CaptureResult.Fail(CaptureStatus.UnsupportedField, "This field is not supported.");
        }

        var identity = BuildIdentity(element, foreground);

        var verdict = Classify(element, identity);
        if (verdict is not null) return verdict;

        AppLog.Info($"Capture starting. process={identity.ProcessName}");

        // 1. A real selection wins, whatever the control type.
        var selection = TryReadSelection(element);
        if (selection is not null)
            return Finish(selection, CaptureMode.Selection, CaptureMethod.UiaTextPattern, false, identity, maxCharacters);

        AppLog.Info("No UI Automation selection; reading the whole field.");

        // 2. Otherwise the whole field.
        var whole = TryReadWholeField(element, out var method);
        if (whole is not null)
            return Finish(whole, CaptureMode.WholeField, method, false, identity, maxCharacters);

        AppLog.Info("UI Automation returned no text; using the clipboard fallback.");

        // 3. UIA gave us nothing usable. The control is already classified safe, so
        //    the clipboard round-trip is allowed.
        return CaptureViaClipboard(identity, maxCharacters);
    }

    // ---- Classification ----------------------------------------------------

    /// <summary>
    /// Returns a failing result when this control must not be touched, or null when
    /// it is positively safe. Anything ambiguous fails closed (ARCHITECTURE.md §10).
    /// </summary>
    private static CaptureResult? Classify(AutomationElement element, TargetIdentity identity)
    {
        AutomationElement.AutomationElementInformation info;
        try
        {
            info = element.Current;
        }
        catch (Exception ex)
        {
            AppLog.Error("Focused element could not be inspected.", ex);
            return CaptureResult.Fail(CaptureStatus.UnsupportedField, "This field is not supported.");
        }

        if (info.IsPassword || IsPasswordWindow(identity.FocusedWindowHandle))
        {
            AppLog.Info("Refused: password field.");
            return CaptureResult.Fail(CaptureStatus.UnsupportedField, "WriteFix never reads password fields.");
        }

        if (!info.IsEnabled)
        {
            AppLog.Info($"Refused: disabled control. type={info.ControlType.ProgrammaticName}");
            return CaptureResult.Fail(CaptureStatus.UnsupportedField, "This field is not editable.");
        }

        var isTextControl =
            info.ControlType == ControlType.Edit ||
            info.ControlType == ControlType.Document ||
            info.ControlType == ControlType.ComboBox;

        var hasTextPattern = element.TryGetCurrentPattern(TextPattern.Pattern, out _);
        var hasValuePattern = element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject);

        if (!isTextControl && !hasTextPattern && !hasValuePattern)
        {
            AppLog.Info($"Refused: unclassifiable control. type={info.ControlType.ProgrammaticName}");
            return CaptureResult.Fail(CaptureStatus.UnsupportedField, "This field is not supported.");
        }

        // A read-only value control can be copied from but never written to, so
        // treat it as unsupported rather than promising a replace we cannot do.
        if (hasValuePattern && valueObject is ValuePattern value && value.Current.IsReadOnly)
        {
            AppLog.Info($"Refused: read-only control. type={info.ControlType.ProgrammaticName}");
            return CaptureResult.Fail(CaptureStatus.UnsupportedField, "This field is read-only.");
        }

        AppLog.Info($"Control accepted. type={info.ControlType.ProgrammaticName} class={info.ClassName} " +
                    $"framework={info.FrameworkId} textPattern={hasTextPattern} valuePattern={hasValuePattern}");
        return null;
    }

    /// <summary>Second opinion for classic Win32 Edit controls, whose UIA IsPassword is not always set.</summary>
    private static bool IsPasswordWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            if ((style & ES_PASSWORD) != 0) return true;

            var className = new StringBuilder(256);
            if (GetClassName(hwnd, className, className.Capacity) == 0) return false;

            return className.ToString().Contains("PASSWORD", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.Error("Password-window check failed; relying on UI Automation alone.", ex);
            return false;
        }
    }

    // ---- UI Automation reads -----------------------------------------------

    private static string? TryReadSelection(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)) return null;
        if (patternObject is not TextPattern text) return null;

        try
        {
            var ranges = text.GetSelection();
            if (ranges is null || ranges.Length == 0) return null;

            var builder = new StringBuilder();
            foreach (var range in ranges) builder.Append(range.GetText(-1));

            var value = builder.ToString();
            AppLog.Info($"UI Automation selection read. ranges={ranges.Length} chars={value.Length}");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (InvalidOperationException)
        {
            // The provider does not support selection; not an error.
            AppLog.Info("UI Automation provider does not support selection.");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading the selection failed.", ex);
            return null;
        }
    }

    private static string? TryReadWholeField(AutomationElement element, out CaptureMethod method)
    {
        method = CaptureMethod.UiaValuePattern;

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
            valueObject is ValuePattern value)
        {
            try
            {
                var text = value.Current.Value;
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch (Exception ex)
            {
                AppLog.Error("ValuePattern read failed.", ex);
            }
        }

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
            patternObject is TextPattern text2)
        {
            try
            {
                var document = text2.DocumentRange.GetText(-1);
                if (!string.IsNullOrWhiteSpace(document))
                {
                    method = CaptureMethod.UiaTextPattern;
                    return document;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("TextPattern document read failed.", ex);
            }
        }

        return null;
    }

    // ---- Guarded clipboard fallback ----------------------------------------

    private CaptureResult CaptureViaClipboard(TargetIdentity identity, int maxCharacters)
    {
        var snapshot = ClipboardGuard.Snapshot();

        try
        {
            // Clearing first is what lets us tell "copied nothing" from "copied the
            // same thing that was already there".
            AppLog.Info($"Clipboard capture starting. snapshotTaken={snapshot is not null}");
            ClipboardGuard.Clear();

            InputSender.SendCtrl(VK_C);
            Thread.Sleep(ClipboardSettleMs);

            var (text, isRich) = ClipboardGuard.ReadText();
            var mode = CaptureMode.Selection;

            if (string.IsNullOrWhiteSpace(text))
            {
                // Nothing was selected — take the whole field.
                AppLog.Info("Clipboard capture: nothing selected; selecting the whole field.");
                InputSender.SendCtrl(VK_A);
                Thread.Sleep(60);
                InputSender.SendCtrl(VK_C);
                Thread.Sleep(ClipboardSettleMs);

                (text, isRich) = ClipboardGuard.ReadText();
                mode = CaptureMode.WholeField;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                AppLog.Info("Clipboard capture: the field copied no text.");
                return CaptureResult.Fail(CaptureStatus.NothingToFix, "Nothing to fix.");
            }

            return Finish(text, mode, CaptureMethod.Clipboard, isRich, identity, maxCharacters);
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Error("Synthetic copy was refused by the target window.", ex);
            return CaptureResult.Fail(CaptureStatus.UnsupportedField, "This field is not supported.");
        }
        finally
        {
            ClipboardGuard.Restore(snapshot);
        }
    }

    private static CaptureResult Finish(
        string text,
        CaptureMode mode,
        CaptureMethod method,
        bool isRich,
        TargetIdentity identity,
        int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            AppLog.Info($"Nothing to fix: captured text is blank. method={method}");
            return CaptureResult.Fail(CaptureStatus.NothingToFix, "Nothing to fix.");
        }

        if (text.Length > maxCharacters)
        {
            AppLog.Info($"Refused: text too long. chars={text.Length} max={maxCharacters} method={method}");
            return CaptureResult.Fail(
                CaptureStatus.TooLong,
                $"That is {text.Length:N0} characters. WriteFix handles up to {maxCharacters:N0} — select a smaller part.");
        }

        AppLog.Info($"Captured. process={identity.ProcessName} mode={mode} method={method} rich={isRich} chars={text.Length}");

        return new CaptureResult
        {
            Status = CaptureStatus.Ok,
            Text = text,
            Mode = mode,
            Method = method,
            IsRichContent = isRich,
            Target = identity,
        };
    }

    // ---- Replacement -------------------------------------------------------

    /// <summary>
    /// Puts <paramref name="corrected"/> back where the original text came from.
    /// Refuses to paste unless the original control is still the focused one.
    /// </summary>
    public ReplaceOutcome Replace(CaptureResult capture, string corrected)
    {
        if (capture.Target is null)
        {
            AppLog.Warn("Refused to paste: the capture has no target.");
            return ReplaceOutcome.Refused("WriteFix lost track of the original field.");
        }

        AppLog.Info($"Replace starting. process={capture.Target.ProcessName} mode={capture.Mode} chars={corrected.Length}");

        try
        {
            if (!RestoreForeground(capture.Target))
            {
                AppLog.Warn("Refused to paste: the original window could not be brought back.");
                return ReplaceOutcome.Refused("WriteFix could not switch back to the original window.");
            }

            var now = ReadCurrentTarget();
            var mismatch = capture.Target.MismatchWith(now);
            if (now is null || mismatch is not null)
            {
                AppLog.Warn($"Refused to paste: focus moved to a different control. reason={mismatch}");
                AppLog.Warn($"Refused to paste: expected {capture.Target.Describe()}");
                AppLog.Warn($"Refused to paste: found {now?.Describe() ?? "nothing"}");
                LogFocusTimeline(capture.Target, now);
                return ReplaceOutcome.Refused("The cursor moved. Use Copy and paste it yourself.");
            }

            var element = AutomationElement.FocusedElement;
            if (element is null || Classify(element, now) is not null)
            {
                AppLog.Warn($"Refused to paste: the field no longer classifies as safe. elementFound={element is not null}");
                return ReplaceOutcome.Refused("That field can no longer be edited safely.");
            }

            return Paste(capture, corrected);
        }
        catch (Exception ex)
        {
            AppLog.Error("Replace failed.", ex);
            return ReplaceOutcome.Refused("WriteFix could not replace the text.");
        }
    }

    private ReplaceOutcome Paste(CaptureResult capture, string corrected)
    {
        var snapshot = ClipboardGuard.Snapshot();

        try
        {
            // Re-select what we captured. A selection generally survives the focus
            // round-trip; a whole-field capture is re-selected explicitly.
            if (capture.Mode == CaptureMode.WholeField)
            {
                InputSender.SendCtrl(VK_A);
                Thread.Sleep(60);
            }

            ClipboardGuard.SetText(corrected);
            Thread.Sleep(60);

            InputSender.SendCtrl(VK_V);
            Thread.Sleep(PasteSettleMs);

            AppLog.Info($"Replaced. process={capture.Target?.ProcessName} mode={capture.Mode}");
            return ReplaceOutcome.Success();
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Error("Synthetic paste was refused by the target window.", ex);
            return ReplaceOutcome.Refused("The target window refused the paste.");
        }
        finally
        {
            // The paste has been consumed by now, so the user's clipboard goes back.
            ClipboardGuard.Restore(snapshot);
        }
    }

    private static bool RestoreForeground(TargetIdentity target)
    {
        if (!IsWindow(target.ForegroundWindow))
        {
            AppLog.Warn("Restore foreground: the original window no longer exists.");
            return false;
        }

        if (IsIconic(target.ForegroundWindow))
        {
            AppLog.Info("Restore foreground: the original window is minimised; restoring it.");
            ShowWindow(target.ForegroundWindow, SW_RESTORE);
        }

        if (GetForegroundWindow() == target.ForegroundWindow)
        {
            AppLog.Info("Restore foreground: already in front.");
            return true;
        }

        // Windows only grants SetForegroundWindow to the thread that owns the
        // current foreground; attaching to it borrows that right.
        var targetThread = GetWindowThreadProcessId(target.ForegroundWindow, out _);
        var ourThread = GetCurrentThreadId();

        var attached = targetThread != ourThread && AttachThreadInput(ourThread, targetThread, true);
        bool requested;
        try
        {
            requested = SetForegroundWindow(target.ForegroundWindow);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, targetThread, false);
        }

        Thread.Sleep(90);
        var restored = GetForegroundWindow() == target.ForegroundWindow;
        AppLog.Info($"Restore foreground: attached={attached} setForeground={requested} restored={restored}");
        return restored;
    }

    /// <summary>
    /// After a refused paste, watches where focus goes for a short while, so the log
    /// tells "focus came back late" apart from "focus is somewhere else". Logs only
    /// changes, and stops as soon as the original field has focus again.
    /// </summary>
    private static void LogFocusTimeline(TargetIdentity expected, TargetIdentity? first)
    {
        var lastSeen = first?.Describe() ?? "nothing";
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < FocusTimelineMs)
        {
            Thread.Sleep(FocusTimelineStepMs);

            var now = ReadCurrentTarget();
            if (expected.SameElementAs(now))
            {
                AppLog.Info($"Focus timeline: the original field had focus again after {stopwatch.ElapsedMilliseconds} ms.");
                return;
            }

            var seen = now?.Describe() ?? "nothing";
            if (seen == lastSeen) continue;

            AppLog.Info($"Focus timeline: t={stopwatch.ElapsedMilliseconds} ms found {seen}");
            lastSeen = seen;
        }

        AppLog.Info($"Focus timeline: the original field did not get focus back within {FocusTimelineMs} ms.");
    }

    private static TargetIdentity? ReadCurrentTarget()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null) return null;

            return BuildIdentity(element, GetForegroundWindow());
        }
        catch (Exception ex)
        {
            AppLog.Error("The currently focused element could not be read.", ex);
            return null;
        }
    }

    private static TargetIdentity BuildIdentity(AutomationElement element, IntPtr foreground)
    {
        var processId = 0;
        var handle = IntPtr.Zero;
        int[] runtimeId = [];

        try { processId = element.Current.ProcessId; }
        catch (Exception ex) { AppLog.Error("Focused element process id unavailable.", ex); }

        try { handle = new IntPtr(element.Current.NativeWindowHandle); }
        catch (Exception ex) { AppLog.Error("Focused element window handle unavailable.", ex); }

        try { runtimeId = element.GetRuntimeId() ?? []; }
        catch (Exception ex) { AppLog.Error("Focused element runtime id unavailable.", ex); }

        var controlType = "unknown";
        var className = "unknown";
        try
        {
            controlType = element.Current.ControlType.ProgrammaticName;
            className = element.Current.ClassName;
        }
        catch (Exception ex) { AppLog.Error("Focused element type unavailable.", ex); }

        return new TargetIdentity
        {
            ForegroundWindow = foreground,
            FocusedWindowHandle = handle,
            ProcessId = processId,
            ProcessName = SafeProcessName(processId),
            RuntimeId = runtimeId,
            ControlType = controlType,
            ClassName = className,
        };
    }

    private static string SafeProcessName(int processId)
    {
        if (processId == 0) return "unknown";

        try
        {
            return Process.GetProcessById(processId).ProcessName;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Process name unavailable. pid={processId}", ex);
            return "unknown";
        }
    }
}
