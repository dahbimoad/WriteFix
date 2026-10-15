namespace WriteFix.Models;

public enum CaptureMode
{
    /// <summary>The user had text selected; only that range is replaced.</summary>
    Selection,

    /// <summary>Nothing was selected; the whole field was taken and is replaced.</summary>
    WholeField,
}

public enum CaptureMethod
{
    UiaTextPattern,
    UiaValuePattern,
    Clipboard,
}

public enum CaptureStatus
{
    Ok,

    /// <summary>Field is genuinely empty, or the selection was whitespace.</summary>
    NothingToFix,

    /// <summary>Password-like, read-only, or a control we could not positively classify. Fail closed.</summary>
    UnsupportedField,

    /// <summary>Beyond the configured input limit (OPEN-QUESTIONS Q6).</summary>
    TooLong,

    /// <summary>Something threw, or the target vanished mid-capture.</summary>
    Failed,
}

/// <summary>
/// Enough identity to find the same control again before Accept pastes into it.
/// Recorded at capture time and re-checked at replace time.
/// </summary>
public sealed class TargetIdentity
{
    public IntPtr ForegroundWindow { get; init; }
    public IntPtr FocusedWindowHandle { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public int[] RuntimeId { get; init; } = [];

    /// <summary>For the log only; never the element's name, which can hold the user's text.</summary>
    public string ControlType { get; init; } = "";
    public string ClassName { get; init; } = "";

    public bool SameElementAs(TargetIdentity? other) => MismatchWith(other) is null;

    /// <summary>Null when <paramref name="other"/> is the same element; otherwise which rule failed, for the log.</summary>
    public string? MismatchWith(TargetIdentity? other)
    {
        if (other is null) return "no focused element";
        if (ProcessId != other.ProcessId) return "different process";

        // Runtime ID is the strongest signal when both sides have one.
        if (RuntimeId.Length > 0 && other.RuntimeId.Length > 0)
            return RuntimeId.AsSpan().SequenceEqual(other.RuntimeId) ? null : "different runtime id";

        // Otherwise fall back to the window handle, which is stable for classic controls.
        if (FocusedWindowHandle == IntPtr.Zero) return "no runtime id and no window handle";
        return FocusedWindowHandle == other.FocusedWindowHandle ? null : "no runtime id and a different window handle";
    }

    public string Describe() =>
        $"process={ProcessName} pid={ProcessId} type={ControlType} class={ClassName} " +
        $"hwnd={FocusedWindowHandle.ToInt64()} runtimeId=[{string.Join(",", RuntimeId)}]";
}

public sealed class CaptureResult
{
    public CaptureStatus Status { get; init; } = CaptureStatus.Failed;
    public string Text { get; init; } = "";
    public CaptureMode Mode { get; init; }
    public CaptureMethod Method { get; init; }

    /// <summary>
    /// True when the source carried real formatting. A whole rich field is Copy-only
    /// so Accept cannot flatten an email signature or a formatted list.
    /// </summary>
    public bool IsRichContent { get; init; }

    public TargetIdentity? Target { get; init; }

    /// <summary>Short, user-facing reason when <see cref="Status"/> is not Ok.</summary>
    public string Message { get; init; } = "";

    public bool CanReplaceAutomatically =>
        Status == CaptureStatus.Ok && !(IsRichContent && Mode == CaptureMode.WholeField);

    public static CaptureResult Fail(CaptureStatus status, string message) =>
        new() { Status = status, Message = message };
}
