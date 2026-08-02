namespace WriteFix.Services.Updates;

/// <summary>The few fields the updater needs out of a GitHub release.</summary>
/// <param name="Version">Tag without its leading v, e.g. <c>1.2.0</c>.</param>
/// <param name="Notes">Release body, shown to the user before they accept.</param>
/// <param name="PageUrl">Where to send a copy that cannot update itself.</param>
/// <param name="InstallerUrl">Download URL of the setup exe, null when none is attached.</param>
public sealed record ReleaseInfo(
    string Version,
    string Notes,
    string PageUrl,
    string? InstallerUrl,
    string? InstallerName,
    long InstallerSize)
{
    /// <summary>A release with no setup exe can only be offered as a web link.</summary>
    public bool HasInstaller => !string.IsNullOrWhiteSpace(InstallerUrl);
}

/// <summary>Outcome of asking GitHub what the newest release is.</summary>
/// <param name="Release">The newer release, or null when there is nothing to install.</param>
public sealed record UpdateCheck(bool Ok, string Message, ReleaseInfo? Release)
{
    public static UpdateCheck Failed(string message) => new(false, message, null);

    public static UpdateCheck UpToDate(string version) =>
        new(true, $"WriteFix {version} is the latest version.", null);

    public static UpdateCheck Available(ReleaseInfo release) =>
        new(true, $"WriteFix {release.Version} is available.", release);
}

/// <summary>How far a download has got. Total is 0 when the server sends no length.</summary>
public readonly record struct DownloadProgress(long BytesDone, long BytesTotal);
