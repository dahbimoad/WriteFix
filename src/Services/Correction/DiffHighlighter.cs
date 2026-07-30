using DiffPlex;

namespace WriteFix.Services.Correction;

public sealed record DiffSegment(string Text, bool IsChanged);

/// <summary>
/// Marks which words of the corrected text differ from the original, so the card
/// can highlight them (FR-10). Added and changed words are marked the same way;
/// deletions are not shown, because the card only displays the corrected version
/// (OPEN-QUESTIONS Q8).
/// </summary>
public static class DiffHighlighter
{
    private static readonly char[] Separators = [' ', '\t', '\n', '\r'];

    public static IReadOnlyList<DiffSegment> Build(string original, string corrected)
    {
        if (string.IsNullOrEmpty(corrected)) return [];
        if (string.IsNullOrEmpty(original)) return [new DiffSegment(corrected, true)];

        try
        {
            return BuildCore(original, corrected);
        }
        catch
        {
            // Highlighting is a nicety; never let it block showing the correction.
            return [new DiffSegment(corrected, false)];
        }
    }

    private static IReadOnlyList<DiffSegment> BuildCore(string original, string corrected)
    {
        var diff = Differ.Instance.CreateWordDiffs(original, corrected, ignoreWhitespace: false, separators: Separators);
        var pieces = diff.PiecesNew;

        // The pieces must rebuild the corrected text exactly, or the highlight would
        // silently alter what the user is about to accept.
        var joiner = ResolveJoiner(pieces, corrected);
        if (joiner is null) return [new DiffSegment(corrected, false)];

        var changed = new bool[pieces.Count];
        foreach (var block in diff.DiffBlocks)
        {
            for (var i = block.InsertStartB; i < block.InsertStartB + block.InsertCountB && i < changed.Length; i++)
                changed[i] = true;
        }

        return Merge(pieces, changed, joiner);
    }

    /// <summary>
    /// Works out how the word pieces reassemble into the original string — either
    /// straight concatenation, or re-inserting the spaces the chunker dropped.
    /// Returns null when neither reproduces the text exactly.
    /// </summary>
    private static string? ResolveJoiner(IReadOnlyList<string> pieces, string corrected)
    {
        if (string.Concat(pieces) == corrected) return "";
        if (string.Join(" ", pieces) == corrected) return " ";
        return null;
    }

    private static List<DiffSegment> Merge(IReadOnlyList<string> pieces, bool[] changed, string joiner)
    {
        var segments = new List<DiffSegment>();

        for (var i = 0; i < pieces.Count; i++)
        {
            var text = i == 0 ? pieces[i] : joiner + pieces[i];

            // Whitespace-only pieces inherit the run they sit in, so highlight blocks
            // stay contiguous instead of flickering word by word.
            var isChanged = string.IsNullOrWhiteSpace(pieces[i])
                ? segments.Count > 0 && segments[^1].IsChanged && LooksChangedAhead(changed, i)
                : changed[i];

            if (segments.Count > 0 && segments[^1].IsChanged == isChanged)
                segments[^1] = segments[^1] with { Text = segments[^1].Text + text };
            else
                segments.Add(new DiffSegment(text, isChanged));
        }

        return segments;
    }

    private static bool LooksChangedAhead(bool[] changed, int index) =>
        index + 1 < changed.Length && changed[index + 1];
}
