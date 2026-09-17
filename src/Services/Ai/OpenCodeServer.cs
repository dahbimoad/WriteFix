using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WriteFix.Services.Logging;
using WriteFix.Services.Platform;

namespace WriteFix.Services.Ai;

/// <summary>
/// Owns a private <c>opencode serve</c> process and authenticated HTTP to it. Started
/// on first use, kept warm for the life of WriteFix, and killed with its whole process
/// tree on exit.
///
/// It listens on loopback only, on a free port, behind a password generated per
/// launch, so no other program on the machine can drive it.
/// </summary>
public sealed class OpenCodeServer : IDisposable
{
    private const string Username = "opencode";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly AuthenticationHeaderValue _authorization;
    private readonly string _password;

    private Process? _process;
    private Uri? _baseUri;

    public OpenCodeServer()
    {
        _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{_password}")));
    }

    /// <summary>
    /// Sends one request to the server, starting it first if needed.
    /// Throws <see cref="OpenCodeException"/> when OpenCode cannot be started.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string pathAndQuery, string? jsonBody, CancellationToken cancellationToken)
    {
        var baseUri = await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, new Uri(baseUri, pathAndQuery));
        request.Headers.Authorization = _authorization;
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Uri> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _baseUri is not null) return _baseUri;

            StopProcess();
            return await StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<Uri> StartAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable()
            ?? throw new OpenCodeException("The SDK is not installed. Install it with: npm i -g opencode-ai");

        var port = FindFreeLoopbackPort();
        var baseUri = new Uri($"http://127.0.0.1:{port}/");

        Directory.CreateDirectory(AppPaths.OpenCodeWorkspace);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppPaths.OpenCodeWorkspace,
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--hostname");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.Environment["OPENCODE_SERVER_USERNAME"] = Username;
        startInfo.Environment["OPENCODE_SERVER_PASSWORD"] = _password;

        var stopwatch = Stopwatch.StartNew();
        _process = Process.Start(startInfo)
            ?? throw new OpenCodeException("The SDK could not be started.");

        await WaitUntilHealthyAsync(_process, baseUri, cancellationToken).ConfigureAwait(false);

        AppLog.Info($"OpenCode server started. pid={_process.Id} port={port} ms={stopwatch.ElapsedMilliseconds}");
        _baseUri = baseUri;
        return baseUri;
    }

    private async Task WaitUntilHealthyAsync(Process process, Uri baseUri, CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(StartupTimeout);

        try
        {
            while (true)
            {
                if (process.HasExited)
                    throw new OpenCodeException($"The SDK stopped while starting (exit code {process.ExitCode}).");

                if (await IsHealthyAsync(baseUri, startup.Token).ConfigureAwait(false)) return;

                await Task.Delay(HealthPollInterval, startup.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopProcess();
            throw new OpenCodeException($"The SDK did not start within {StartupTimeout.TotalSeconds:0}s.");
        }
    }

    private async Task<bool> IsHealthyAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "global/health"));
        request.Headers.Authorization = _authorization;

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // Not listening yet; the caller polls again.
            return false;
        }
    }

    /// <summary>
    /// The npm package installs a <c>opencode.cmd</c> shim on PATH. Launching through
    /// the shim leaves the real <c>opencode.exe</c> running when the shim is killed,
    /// so WriteFix starts the binary itself.
    /// </summary>
    private static string? FindExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var direct = Path.Combine(directory, "opencode.exe");
            if (File.Exists(direct)) return direct;

            var npmBinary = Path.Combine(directory, "node_modules", "opencode-ai", "bin", "opencode.exe");
            if (File.Exists(npmBinary)) return npmBinary;
        }

        return null;
    }

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void StopProcess()
    {
        _baseUri = null;
        if (_process is null) return;

        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            AppLog.Info("OpenCode server stopped.");
        }
        catch (Win32Exception ex)
        {
            AppLog.Error("OpenCode server could not be stopped.", ex);
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        StopProcess();
        _http.Dispose();
        _startGate.Dispose();
    }
}

/// <summary>A failure talking to OpenCode whose message is safe and useful to show the user.</summary>
public sealed class OpenCodeException(string message) : Exception(message);
