using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using WriteFix.Services.Logging;

namespace WriteFix.Services.Updates;

/// <summary>
/// Checks GitHub Releases for a newer WriteFix and installs it on request.
///
/// The whole mechanism is the next setup exe run silently over the top: same
/// AppId, same per-user folder, so no admin prompt and nothing to uninstall
/// first. Nothing here decides on its own to check or install; the caller does,
/// because an app that updates itself behind the user's back is not something
/// they agreed to.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/dahbimoad/WriteFix/releases/latest";
    private const string InstallerNamePrefix = "writefix-setup";

    /// <summary>Inno records a per-user install under "{AppId}_is1".</summary>
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C3F1A64-9E2B-4D58-B0A7-5F1C6E8D2A93}_is1";

    public const string ReleasesPageUrl = "https://github.com/dahbimoad/WriteFix/releases/latest";

    /// <summary>How long a manual check may sit there before giving up.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http = new();

    public UpdateService()
    {
        // GitHub rejects requests with no User-Agent.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WriteFix", CurrentVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request CTS controls this instead
    }

    /// <summary>This build's version, e.g. <c>1.2.0</c>. The csproj is the single source.</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);

        try
        {
            using var response = await _http.GetAsync(LatestReleaseUrl, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateCheck.Failed($"GitHub replied {(int)response.StatusCode}.");

            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var release = ParseRelease(payload);

            AppLog.Info($"Update check finished. latest={release.Version} current={CurrentVersion}");

            return IsNewer(release.Version, CurrentVersion)
                ? UpdateCheck.Available(release)
                : UpdateCheck.UpToDate(CurrentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheck.Failed("Cancelled.");
        }
        catch (OperationCanceledException)
        {
            return UpdateCheck.Failed("The check timed out.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("Update check could not reach GitHub.", ex);
            return UpdateCheck.Failed("Could not reach GitHub. Check your internet.");
        }
        catch (JsonException ex)
        {
            AppLog.Error("Update check got a response WriteFix could not read.", ex);
            return UpdateCheck.Failed("GitHub sent a response WriteFix could not read.");
        }
    }

    /// <summary>Streams the setup exe to the temp folder and returns its path.</summary>
    public async Task<string> DownloadAsync(
        ReleaseInfo release,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(
            Path.GetTempPath(),
            release.InstallerName ?? $"WriteFix-Setup-{release.Version}.exe");

        using var response = await _http
            .GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
        await CopyToFileAsync(response, target, total, progress, cancellationToken).ConfigureAwait(false);

        AppLog.Info($"Update {release.Version} downloaded to {target}");
        return target;
    }

    private static async Task CopyToFileAsync(
        HttpResponseMessage response,
        string target,
        long total,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(target);

        var buffer = new byte[64 * 1024];
        long done = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;
            progress.Report(new DownloadProgress(done, total));
        }

        if (total > 0 && done != total)
            throw new IOException($"The download stopped at {done} of {total} bytes.");
    }

    /// <summary>
    /// Hands over to setup and returns; the caller must then shut the app down,
    /// because setup cannot replace files a running WriteFix is holding open.
    /// </summary>
    public static void LaunchInstaller(string installerPath, bool relaunchInBackground)
    {
        var start = new ProcessStartInfo(installerPath) { UseShellExecute = false };
        foreach (var argument in InstallerArguments(relaunchInBackground))
            start.ArgumentList.Add(argument);

        Process.Start(start);
        AppLog.Info($"Installer started: {installerPath}");
    }

    /// <summary>
    /// /SILENT still shows setup's own progress window, which is the honest thing
    /// to show while the app is being replaced. The task overrides matter: a silent
    /// run would otherwise apply the installer's default task selection and quietly
    /// switch "Start with Windows" back on for someone who had turned it off.
    /// </summary>
    internal static IReadOnlyList<string> InstallerArguments(bool relaunchInBackground)
    {
        var arguments = new List<string>
        {
            "/SILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/CLOSEAPPLICATIONS",
            "/MERGETASKS=!startup,!desktopicon",
            "/RELAUNCH=1",
        };

        if (relaunchInBackground)
            arguments.Add("/BACKGROUND=1");

        return arguments;
    }

    /// <summary>
    /// Whether this copy came from the installer, and so can be replaced in place.
    /// A folder someone copied by hand has no uninstaller and must not be handed a
    /// silent setup: it would install a second copy elsewhere and leave this one stale.
    /// </summary>
    public static bool InstalledBySetup()
    {
        using var perUser = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (perUser is not null) return true;

        using var perMachine = Registry.LocalMachine.OpenSubKey(UninstallKey);
        if (perMachine is not null) return true;

        var folder = AppContext.BaseDirectory;
        return Directory.EnumerateFiles(folder, "unins*.exe").Any();
    }

    /// <summary>"v1.2.0" -> (1, 2, 0). Junk sorts low rather than throwing, so one
    /// odd tag on GitHub can never break the check.</summary>
    internal static Version ParseVersion(string? text)
    {
        var cleaned = (text ?? "").Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];
        return Version.TryParse(cleaned, out var parsed) ? parsed : new Version(0, 0, 0);
    }

    internal static bool IsNewer(string candidate, string current) =>
        ParseVersion(candidate) > ParseVersion(current);

    internal static ReleaseInfo ParseRelease(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var tag = ReadString(root, "tag_name") ?? ReadString(root, "name") ?? "";
        var installer = FindInstallerAsset(root);

        return new ReleaseInfo(
            Version: tag.Trim().TrimStart('v', 'V'),
            Notes: (ReadString(root, "body") ?? "").Trim(),
            PageUrl: ReadString(root, "html_url") ?? ReleasesPageUrl,
            InstallerUrl: installer is null ? null : ReadString(installer.Value, "browser_download_url"),
            InstallerName: installer is null ? null : ReadString(installer.Value, "name"),
            InstallerSize: installer is null ? 0 : ReadLong(installer.Value, "size"));
    }

    /// <summary>The setup exe among the release files, ignoring checksums and extras.</summary>
    private static JsonElement? FindInstallerAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = ReadString(asset, "name") ?? "";
            if (name.StartsWith(InstallerNamePrefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return asset;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static string ReadCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public void Dispose() => _http.Dispose();
}
