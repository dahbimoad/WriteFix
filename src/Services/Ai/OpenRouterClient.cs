using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WriteFix.Models;
using WriteFix.Services.Logging;
using WriteFix.Services.Settings;

namespace WriteFix.Services.Ai;

/// <summary>
/// Talks to OpenRouter's Chat Completions endpoint with a plain HttpClient — no AI
/// SDK (ARCHITECTURE.md §2). Non-streaming: the card shows "Working…" meanwhile.
///
/// Only the system prompt and the captured text ever leave this machine. No window
/// titles, process names, or diagnostic identifiers are sent.
/// </summary>
public sealed class OpenRouterClient : IDisposable
{
    private const string ChatCompletionsUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string KeyUrl = "https://openrouter.ai/api/v1/key";

    private readonly HttpClient _http = new();
    private readonly SettingsStore _settings;
    private readonly SecretStore _secrets;

    public OpenRouterClient(SettingsStore settings, SecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request CTS controls this instead
    }

    public async Task<CorrectionResult> CorrectAsync(string text, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        var apiKey = _secrets.Read();
        if (string.IsNullOrWhiteSpace(apiKey))
            return CorrectionResult.Error("No OpenRouter API key yet. Open Settings to add one.");

        var body = BuildRequestBody(settings, text);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            AppLog.Info($"Correction request finished. status={(int)response.StatusCode} ms={stopwatch.ElapsedMilliseconds} model={settings.Model}");

            return response.IsSuccessStatusCode
                ? ParseCompletion(payload)
                : CorrectionResult.Error(DescribeHttpFailure(response.StatusCode, payload, settings));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user cancelled or pressed the hotkey again; not an error.
            return CorrectionResult.Error("Cancelled.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Warn($"Correction timed out after {settings.RequestTimeoutSeconds}s.");
            return CorrectionResult.Error($"Timed out after {settings.RequestTimeoutSeconds}s. Try Regenerate.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("Correction request could not reach OpenRouter.", ex);
            return CorrectionResult.Error("No connection to OpenRouter. Check your internet.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Correction request failed unexpectedly.", ex);
            return CorrectionResult.Error("Something went wrong reaching the AI.");
        }
    }

    /// <summary>Verifies the key without sending any message text.</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "Enter an API key first.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, KeyUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            AppLog.Info($"Test connection finished. status={(int)response.StatusCode}");

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (false, "That key was rejected by OpenRouter.");

            if (!response.IsSuccessStatusCode)
                return (false, $"OpenRouter replied {(int)response.StatusCode}.");

            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return (true, DescribeKey(payload));
        }
        catch (OperationCanceledException)
        {
            return (false, "The check timed out.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("Test connection could not reach OpenRouter.", ex);
            return (false, "Could not reach OpenRouter. Check your internet.");
        }
    }

    private static string BuildRequestBody(AppSettings settings, string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", settings.Model);
            writer.WriteBoolean("stream", false);

            writer.WriteStartArray("messages");

            writer.WriteStartObject();
            writer.WriteString("role", "system");
            // Fixed contract + the user's style rules, composed by AppSettings.
            writer.WriteString("content", settings.BuildSystemPrompt());
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", text);
            writer.WriteEndObject();

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static CorrectionResult ParseCompletion(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            // OpenRouter can return an error object inside a 200 response.
            if (root.TryGetProperty("error", out var error))
                return CorrectionResult.Error(DescribeErrorObject(error));

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return CorrectionResult.Error("The AI returned an empty response. Try Regenerate.");
            }

            var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            var cleaned = CleanModelOutput(content);

            return string.IsNullOrWhiteSpace(cleaned)
                ? CorrectionResult.Error("The AI returned nothing. Try Regenerate.")
                : CorrectionResult.Ok(cleaned);
        }
        catch (Exception ex)
        {
            AppLog.Error("Response body could not be parsed.", ex);
            return CorrectionResult.Error("The AI sent a response WriteFix could not read.");
        }
    }

    /// <summary>
    /// Models sometimes wrap the answer in a markdown fence despite being told not
    /// to. Unwrapping is safe; anything else is left exactly as the model wrote it.
    /// </summary>
    private static string CleanModelOutput(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstBreak = text.IndexOf('\n');
        if (firstBreak < 0) return text;

        var closing = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closing <= firstBreak) return text;

        return text[(firstBreak + 1)..closing].Trim();
    }

    private static string DescribeHttpFailure(HttpStatusCode status, string payload, AppSettings settings)
    {
        var detail = TryReadErrorMessage(payload);

        return status switch
        {
            HttpStatusCode.Unauthorized => "Your OpenRouter API key was rejected. Check it in Settings.",
            HttpStatusCode.PaymentRequired => "Your OpenRouter account is out of credit.",
            // OpenRouter's 404 body is genuinely useful here — it names the right slug
            // when a :free model has moved to paid-only.
            HttpStatusCode.NotFound => string.IsNullOrWhiteSpace(detail)
                ? $"Model \"{settings.Model}\" was not found. Pick another in Settings."
                : detail,
            HttpStatusCode.TooManyRequests =>
                settings.Model.EndsWith(":free", StringComparison.OrdinalIgnoreCase)
                    ? "Free-model rate limit reached. Wait a minute, or switch to a paid model in Settings."
                    : "Rate limited by OpenRouter. Wait a moment and try Regenerate.",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "OpenRouter timed out. Try Regenerate.",
            >= HttpStatusCode.InternalServerError => "OpenRouter had a server problem. Try Regenerate.",
            _ => string.IsNullOrWhiteSpace(detail) ? $"OpenRouter replied {(int)status}." : detail,
        };
    }

    /// <summary>
    /// Pulls only the provider's short error string out of a failure body. The rest
    /// of the payload is never logged or shown.
    /// </summary>
    private static string TryReadErrorMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("error", out var error)
                ? DescribeErrorObject(error)
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static string DescribeErrorObject(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "The AI returned an error.";

        var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
        return string.IsNullOrWhiteSpace(message) ? "The AI returned an error." : message!;
    }

    private static string DescribeKey(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("data", out var data)) return "Key works.";

            var limit = data.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
                ? l.GetDouble()
                : (double?)null;
            var usage = data.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Number
                ? u.GetDouble()
                : (double?)null;

            if (limit is null) return usage is null ? "Key works." : $"Key works. Used ${usage:0.00} so far.";

            var remaining = Math.Max(0, limit.Value - (usage ?? 0));
            return $"Key works. ${remaining:0.00} of ${limit:0.00} credit left.";
        }
        catch
        {
            return "Key works.";
        }
    }

    public void Dispose() => _http.Dispose();
}
