namespace WriteFix.Models;

/// <summary>
/// The result of putting corrected text back into the original field.
/// <c>Replaced == false</c> is a deliberate refusal, not a crash — the caller falls
/// back to the clipboard so the correction is never lost.
/// </summary>
public sealed record ReplaceOutcome(bool Replaced, string Message)
{
    public static ReplaceOutcome Success() => new(true, "");

    public static ReplaceOutcome Refused(string message) => new(false, message);
}
