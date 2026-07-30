using System.Windows;
using WriteFix.Services.Logging;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;

namespace WriteFix.Services.Capture;

/// <summary>
/// The clipboard belongs to the user, not to us. WriteFix borrows it to copy and
/// paste, so every borrow is bracketed by a snapshot and a restore.
///
/// Every method here must run on an STA thread.
/// </summary>
public static class ClipboardGuard
{
    private const int Attempts = 10;
    private const int RetryDelayMs = 60;

    /// <summary>
    /// Copies what is currently on the clipboard so it can be put back later.
    /// Returns null when the clipboard is empty or unreadable.
    /// </summary>
    public static DataObject? Snapshot()
    {
        return Retry(() =>
        {
            var source = Clipboard.GetDataObject();
            if (source is null) return null;

            var copy = new DataObject();
            var kept = 0;

            foreach (var format in source.GetFormats(autoConvert: false))
            {
                try
                {
                    var data = source.GetData(format, autoConvert: false);
                    if (data is null) continue;
                    copy.SetData(format, data);
                    kept++;
                }
                catch
                {
                    // Some formats (delayed-render, cross-process handles) simply
                    // cannot be read. Losing one is better than failing the capture.
                }
            }

            return kept > 0 ? copy : null;
        }, nameof(Snapshot));
    }

    public static void Restore(DataObject? snapshot)
    {
        Retry<object?>(() =>
        {
            if (snapshot is null)
                Clipboard.Clear();
            else
                Clipboard.SetDataObject(snapshot, copy: true);

            return null;
        }, nameof(Restore));
    }

    public static void SetText(string text)
    {
        Retry<object?>(() =>
        {
            // copy:true flushes to the OS so the clipboard survives our process.
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            Clipboard.SetDataObject(data, copy: true);
            return null;
        }, nameof(SetText));
    }

    public static void Clear() => Retry<object?>(() =>
    {
        Clipboard.Clear();
        return null;
    }, nameof(Clear));

    /// <summary>Reads the clipboard as plain text plus a verdict on whether it carried real formatting.</summary>
    public static (string Text, bool IsRich) ReadText()
    {
        return Retry(() =>
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return ("", false);

            var text = data.GetDataPresent(DataFormats.UnicodeText)
                ? data.GetData(DataFormats.UnicodeText) as string ?? ""
                : "";

            return (text, LooksRich(data, text));
        }, nameof(ReadText));
    }

    /// <summary>
    /// Decides whether replacing this content as plain text would destroy formatting.
    /// Chat boxes routinely publish an HTML flavour that is only a wrapper around
    /// plain text — that is still safe to replace. Real structure is not.
    /// </summary>
    private static bool LooksRich(IDataObject data, string plainText)
    {
        if (data.GetDataPresent(DataFormats.Rtf))
        {
            try
            {
                if (data.GetData(DataFormats.Rtf) is string rtf && HasRtfStructure(rtf)) return true;
            }
            catch { /* unreadable flavour, fall through to the HTML check */ }
        }

        if (!data.GetDataPresent(DataFormats.Html)) return false;

        try
        {
            if (data.GetData(DataFormats.Html) is not string html) return false;
            return HasHtmlStructure(html, plainText);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasRtfStructure(string rtf) =>
        rtf.Contains("\\pict", StringComparison.OrdinalIgnoreCase) ||
        rtf.Contains("\\b ", StringComparison.Ordinal) ||
        rtf.Contains("\\i ", StringComparison.Ordinal) ||
        rtf.Contains("\\ul", StringComparison.Ordinal) ||
        rtf.Contains("\\trowd", StringComparison.OrdinalIgnoreCase);

    private static bool HasHtmlStructure(string html, string plainText)
    {
        // Tags that carry meaning we would lose by flattening to plain text.
        string[] structural =
        [
            "<img", "<table", "<ul", "<ol", "<li", "<a ", "<b>", "<strong", "<i>", "<em",
            "<u>", "<h1", "<h2", "<h3", "<blockquote", "<pre", "<code",
        ];

        if (structural.Any(tag => html.Contains(tag, StringComparison.OrdinalIgnoreCase))) return true;

        // Inline styling that is not just the wrapper Chromium adds around a paste.
        return html.Contains("style=", StringComparison.OrdinalIgnoreCase) &&
               plainText.Length > 0 &&
               html.Length > plainText.Length * 6;
    }

    private static T? Retry<T>(Func<T?> action, string operation)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception) when (attempt < Attempts)
            {
                // Another process has the clipboard open; it will let go shortly.
                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Clipboard {operation} failed after {Attempts} attempts.", ex);
                return default;
            }
        }

        return default;
    }
}
