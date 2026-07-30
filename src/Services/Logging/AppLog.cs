using System.IO;
using System.Text;
using WriteFix.Services.Platform;

namespace WriteFix.Services.Logging;

/// <summary>
/// Small append-only log. Deliberately dumb: no framework, no sinks, no async.
///
/// PRIVACY CONTRACT (ARCHITECTURE.md §9) — never pass any of these to this class:
/// original or corrected message text, the system prompt, clipboard contents, API
/// keys, authorization headers, or HTTP request/response bodies. Log state, codes,
/// durations and process names only.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly Lock Gate = new();

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null)
    {
        // Type and stack only. An exception Message can contain a URL, a file path,
        // or echoed content, so it is not written.
        var detail = ex is null ? message : $"{message} | {ex.GetType().FullName} | {FirstFrames(ex)}";
        Write("ERR", detail);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.EnsureCreated();
                Rotate();
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {Sanitize(message)}{Environment.NewLine}";
                File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down (ARCHITECTURE.md §9).
        }
    }

    private static void Rotate()
    {
        var info = new FileInfo(AppPaths.LogFile);
        if (!info.Exists || info.Length < MaxBytes) return;

        File.Delete(AppPaths.LogBackupFile);
        File.Move(AppPaths.LogFile, AppPaths.LogBackupFile);
    }

    /// <summary>Keeps one line per entry so a stray newline cannot forge log records.</summary>
    private static string Sanitize(string value)
    {
        var flat = value.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length > 800 ? flat[..800] + "…" : flat;
    }

    private static string FirstFrames(Exception ex)
    {
        var stack = ex.StackTrace;
        if (string.IsNullOrEmpty(stack)) return "no-stack";

        var frames = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(4);
        return string.Join(" ⇐ ", frames.Select(f => f.Trim()));
    }
}
