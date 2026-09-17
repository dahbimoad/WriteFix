namespace WriteFix.Services.Ai;

/// <summary>Tidies text returned by any model before it is shown or pasted.</summary>
public static class ModelOutput
{
    /// <summary>
    /// Models sometimes wrap the answer in a markdown fence despite being told not
    /// to. Unwrapping is safe; anything else is left exactly as the model wrote it.
    /// </summary>
    public static string Clean(string content)
    {
        var text = StripReasoning(content).Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstBreak = text.IndexOf('\n');
        if (firstBreak < 0) return text;

        var closing = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closing <= firstBreak) return text;

        return text[(firstBreak + 1)..closing].Trim();
    }

    /// <summary>
    /// Drops a leading <c>&lt;think&gt;…&lt;/think&gt;</c> block. Well-behaved reasoning
    /// models return their scratchpad in a separate <c>message.reasoning</c> field,
    /// which WriteFix never reads — but some (Qwen among them) inline it into the
    /// answer instead, which would otherwise be pasted straight into the user's text
    /// box. Only a block at the very start is removed, and only when it is closed, so
    /// a message that legitimately talks about a &lt;think&gt; tag survives intact.
    /// </summary>
    private static string StripReasoning(string content)
    {
        const string Open = "<think>";
        const string Close = "</think>";

        var text = content.TrimStart();
        if (!text.StartsWith(Open, StringComparison.OrdinalIgnoreCase)) return content;

        var close = text.IndexOf(Close, StringComparison.OrdinalIgnoreCase);
        return close < 0 ? content : text[(close + Close.Length)..];
    }
}
