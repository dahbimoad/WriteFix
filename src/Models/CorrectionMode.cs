namespace WriteFix.Models;

/// <summary>What WriteFix does to the captured text.</summary>
public enum CorrectionMode
{
    /// <summary>Fix errors while keeping the user's own wording.</summary>
    Fix,

    /// <summary>Rewrite the text freely, strictly following the user's rephrase instructions.</summary>
    Rephrase,
}
