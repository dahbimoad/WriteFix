using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WriteFix.Models;
using WriteFix.Services.Logging;
using WriteFix.Services.Platform;
using WriteFix.Services.Settings;

namespace WriteFix.Services.Ai;

/// <summary>
/// Corrects text through the OpenCode server API (opencode.ai/docs/server) — the same
/// HTTP API the JavaScript SDK wraps, called directly because WriteFix is .NET.
///
/// OpenCode is a coding agent that can run shell commands and edit files, and the
/// captured message is untrusted text. So every request uses a throwaway session
/// that denies every permission, with every tool switched off, deleted afterwards.
/// </summary>
public sealed class OpenCodeClient
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly OpenCodeServer _server;
    private readonly SettingsStore _settings;

    public OpenCodeClient(OpenCodeServer server, SettingsStore settings)
    {
        _server = server;
        _settings = settings;
    }

    private static string DirectoryQuery => "?directory=" + Uri.EscapeDataString(AppPaths.OpenCodeWorkspace);

    public async Task<CorrectionResult> CorrectAsync(string text, CorrectionMode mode, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        if (!TrySplitModel(settings.OpenCodeModel, out var providerId, out var modelId))
            return CorrectionResult.Error("Pick an SDK model in Settings, written as provider/model.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));

        var stopwatch = Stopwatch.StartNew();
        string? sessionId = null;

        try
        {
            sessionId = await CreateSessionAsync(timeout.Token).ConfigureAwait(false);

            var body = BuildPromptBody(providerId, modelId, settings.BuildSystemPrompt(mode), text);
            var payload = await SendForJsonAsync(HttpMethod.Post, $"session/{sessionId}/message{DirectoryQuery}", body, timeout.Token)
                .ConfigureAwait(false);

            AppLog.Info($"OpenCode correction finished. ms={stopwatch.ElapsedMilliseconds} model={settings.OpenCodeModel} mode={mode}");
            return ParseReply(payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CorrectionResult.Error("Cancelled.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Warn($"OpenCode correction timed out after {settings.RequestTimeoutSeconds}s.");
            return CorrectionResult.Error($"Timed out after {settings.RequestTimeoutSeconds}s. Try Regenerate.");
        }
        catch (OpenCodeException ex)
        {
            AppLog.Warn("OpenCode correction failed with a reported error.");
            return CorrectionResult.Error(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("OpenCode correction could not reach the local server.", ex);
            return CorrectionResult.Error("Lost the connection to the SDK. Try Regenerate.");
        }
        catch (Exception ex) when (IsUnreadableResponse(ex))
        {
            AppLog.Error("OpenCode response could not be parsed.", ex);
            return CorrectionResult.Error("The SDK sent a response WriteFix could not read.");
        }
        finally
        {
            if (sessionId is not null) await DiscardSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts OpenCode if needed and lists every model of every connected provider.
    /// On failure the list is empty and Error says why, in words fit for the user.
    /// </summary>
    public async Task<(IReadOnlyList<string> Models, string Error)> LoadModelsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        try
        {
            var models = await ListModelsAsync(timeout.Token).ConfigureAwait(false);
            return models.Count == 0
                ? ([], "The SDK is running, but no provider is connected. Run: opencode auth login")
                : (models, "");
        }
        catch (OperationCanceledException)
        {
            return ([], "The SDK did not answer in time.");
        }
        catch (OpenCodeException ex)
        {
            return ([], ex.Message);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("OpenCode model list could not reach the local server.", ex);
            return ([], "Could not reach the SDK.");
        }
        catch (Exception ex) when (IsUnreadableResponse(ex))
        {
            AppLog.Error("OpenCode model list could not be parsed.", ex);
            return ([], "The SDK sent a model list WriteFix could not read.");
        }
    }

    /// <summary>Reports whether OpenCode can serve <paramref name="model"/>, without sending any message text.</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(string model, CancellationToken cancellationToken)
    {
        var (models, error) = await LoadModelsAsync(cancellationToken).ConfigureAwait(false);
        if (models.Count == 0) return (false, error);

        var wanted = model.Trim();
        return models.Contains(wanted, StringComparer.Ordinal)
            ? (true, $"The SDK works. {models.Count} models available, including {wanted}.")
            : (false, $"The SDK works, but \"{wanted}\" is not one of its {models.Count} connected models. Load the list and pick one.");
    }

    /// <summary>A response that parsed as JSON but did not have the shape the OpenCode API documents.</summary>
    private static bool IsUnreadableResponse(Exception ex) =>
        ex is JsonException or KeyNotFoundException or InvalidOperationException;

    private async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var payload = await SendForJsonAsync(HttpMethod.Get, $"provider{DirectoryQuery}", null, cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var connected = root.GetProperty("connected").EnumerateArray()
            .Select(id => id.GetString())
            .ToHashSet(StringComparer.Ordinal);

        return root.GetProperty("all").EnumerateArray()
            .Where(provider => connected.Contains(provider.GetProperty("id").GetString()))
            .SelectMany(provider => provider.GetProperty("models").EnumerateObject()
                .Select(model => $"{provider.GetProperty("id").GetString()}/{model.Name}"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string> CreateSessionAsync(CancellationToken cancellationToken)
    {
        var body = WriteJson(writer =>
        {
            writer.WriteString("title", "WriteFix");
            writer.WriteStartArray("permission");
            writer.WriteStartObject();
            writer.WriteString("permission", "*");
            writer.WriteString("pattern", "*");
            writer.WriteString("action", "deny");
            writer.WriteEndObject();
            writer.WriteEndArray();
        });

        var payload = await SendForJsonAsync(HttpMethod.Post, $"session{DirectoryQuery}", body, cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()
            ?? throw new OpenCodeException("The SDK did not return a session.");
    }

    private static string BuildPromptBody(string providerId, string modelId, string systemPrompt, string text) =>
        WriteJson(writer =>
        {
            writer.WriteStartObject("model");
            writer.WriteString("providerID", providerId);
            writer.WriteString("modelID", modelId);
            writer.WriteEndObject();

            // Fixed contract + the user's instructions, composed by AppSettings.
            writer.WriteString("system", systemPrompt);

            writer.WriteStartObject("tools");
            writer.WriteBoolean("*", false);
            writer.WriteEndObject();

            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
        });

    /// <summary>
    /// Stops any generation still running and deletes the session, so corrected
    /// messages do not pile up in OpenCode's history. Best effort: a failure here must
    /// not replace the correction result, so it is logged and dropped.
    /// </summary>
    private async Task DiscardSessionAsync(string sessionId)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);

        try
        {
            using var abort = await _server.SendAsync(HttpMethod.Post, $"session/{sessionId}/abort{DirectoryQuery}", null, cleanup.Token)
                .ConfigureAwait(false);
            using var delete = await _server.SendAsync(HttpMethod.Delete, $"session/{sessionId}{DirectoryQuery}", null, cleanup.Token)
                .ConfigureAwait(false);

            if (!delete.IsSuccessStatusCode)
                AppLog.Warn($"OpenCode session could not be deleted. status={(int)delete.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or OpenCodeException)
        {
            AppLog.Error("OpenCode session cleanup failed.", ex);
        }
    }

    private async Task<string> SendForJsonAsync(HttpMethod method, string pathAndQuery, string? body, CancellationToken cancellationToken)
    {
        using var response = await _server.SendAsync(method, pathAndQuery, body, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warn($"OpenCode request failed. endpoint={pathAndQuery.Split('/', '?')[0]} status={(int)response.StatusCode}");
            throw new OpenCodeException(DescribeHttpFailure(response.StatusCode, payload));
        }

        return payload;
    }

    private static CorrectionResult ParseReply(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        // A provider failure arrives as a 200 whose assistant message carries an error.
        if (root.TryGetProperty("info", out var info) &&
            info.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            return CorrectionResult.Error(DescribeMessageError(error));
        }

        // Only visible text parts are the answer; reasoning, step markers and tool
        // parts are the agent's working, never the correction.
        var reply = new StringBuilder();
        foreach (var part in root.GetProperty("parts").EnumerateArray())
        {
            if (IsVisibleText(part)) reply.Append(part.GetProperty("text").GetString());
        }

        var cleaned = ModelOutput.Clean(reply.ToString());
        return string.IsNullOrWhiteSpace(cleaned)
            ? CorrectionResult.Error("The SDK returned nothing. Try Regenerate.")
            : CorrectionResult.Ok(cleaned);
    }

    private static bool IsVisibleText(JsonElement part) =>
        part.TryGetProperty("type", out var type) && type.GetString() == "text" &&
        !IsTrue(part, "synthetic") && !IsTrue(part, "ignored");

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string DescribeMessageError(JsonElement error)
    {
        var name = error.TryGetProperty("name", out var n) ? n.GetString() : null;
        var message = error.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var m)
            ? m.GetString()
            : null;

        return name switch
        {
            "ProviderAuthError" => "The SDK is not signed in to that provider. Run: opencode auth login",
            "MessageOutputLengthError" => "The reply was too long and got cut off. Try a shorter text.",
            _ => string.IsNullOrWhiteSpace(message) ? "The SDK reported an error. Try Regenerate." : message,
        };
    }

    /// <summary>Pulls only a short error string out of a failure body. The rest is never logged or shown.</summary>
    private static string DescribeHttpFailure(HttpStatusCode status, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("message", out var dataMessage) && dataMessage.ValueKind == JsonValueKind.String)
                {
                    return dataMessage.GetString()!;
                }

                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to the status code.
        }

        return $"The SDK replied {(int)status}.";
    }

    private static bool TrySplitModel(string model, out string providerId, out string modelId)
    {
        // Model ids may themselves contain '/', e.g. "openrouter/google/gemma", so only the first one splits.
        var separator = model.IndexOf('/');
        providerId = separator > 0 ? model[..separator].Trim() : "";
        modelId = separator > 0 ? model[(separator + 1)..].Trim() : "";
        return providerId.Length > 0 && modelId.Length > 0;
    }

    private static string WriteJson(Action<Utf8JsonWriter> writeProperties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
