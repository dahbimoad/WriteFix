namespace WriteFix.Services.Ai;

public sealed record CorrectionResult(bool Success, string Text, string ErrorMessage)
{
    public static CorrectionResult Ok(string text) => new(true, text, "");
    public static CorrectionResult Error(string message) => new(false, "", message);
}
