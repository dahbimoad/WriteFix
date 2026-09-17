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
/// Talks to an OpenAI-compatible Chat Completions endpoint with a plain HttpClient —
/// no AI SDK (ARCHITECTURE.md §2). Non-streaming: the card shows "Working…" meanwhile.
///
/// The provider is whatever <see cref="AppSettings.ApiBaseUrl"/> points at. Groq,
/// OpenRouter, Mistral and a local Ollama all speak this same request shape, so
/// switching between them is a URL change rather than a code change.
///
/// Only the system prompt and the captured text ever leave this machine. No window
/// titles, process names, or diagnostic identifiers are sent.
/// </summary>
public sealed class ChatCompletionsClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly SettingsStore _settings;
    private readonly SecretStore _secrets;

    public ChatCompletionsClient(SettingsStore settings, SecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request CTS controls this instead
    }

    public async Task<CorrectionResult> CorrectAsync(string text, CorrectionMode mode, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        var apiKey = _secrets.Read();
        if (string.IsNullOrWhiteSpace(apiKey))
            return CorrectionResult.Error("No API key yet. Open Settings to add one.");

        var body = BuildRequestBody(settings.Model, settings.BuildSystemPrompt(mode), text);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CompletionsUrl(settings.ApiBaseUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            AppLog.Info($"Correction request finished. status={(int)response.StatusCode} " +
                        $"ms={stopwatch.ElapsedMilliseconds} model={settings.Model} mode={mode}");

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
            AppLog.Error("Correction request could not reach the AI provider.", ex);
            return CorrectionResult.Error("No connection to the AI provider. Check your internet.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Correction request failed unexpectedly.", ex);
            return CorrectionResult.Error("Something went wrong reaching the AI.");
        }
    }

    /// <summary>
    /// Verifies the key against the provider the user is about to save rather than
    /// the one already stored, because the base URL and the model box may both have
    /// been edited since the last save.
    ///
    /// There is no "check my key" endpoint shared across providers — OpenRouter has
    /// one, Groq does not — so this sends the smallest possible completion instead.
    /// It carries no message text: the prompt is the single word "ping" and the reply
    /// is capped at one token.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(
        string baseUrl,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "Enter an API key first.");
        if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "Enter an API base URL first.");
        if (string.IsNullOrWhiteSpace(model)) return (false, "Enter a model first.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CompletionsUrl(baseUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(BuildPingBody(model), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            AppLog.Info($"Test connection finished. status={(int)response.StatusCode} model={model}");

            if (response.IsSuccessStatusCode) return (true, $"Key works with {model}.");

            // Same wording the correction path uses, so one failure reads one way.
            var probed = _settings.Current.Clone();
            probed.Model = model;
            return (false, DescribeHttpFailure(response.StatusCode, payload, probed));
        }
        catch (OperationCanceledException)
        {
            return (false, "The check timed out.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("Test connection could not reach the AI provider.", ex);
            return (false, "Could not reach that URL. Check the address and your internet.");
        }
    }

    /// <summary>Providers differ in base URL; the path underneath it does not.</summary>
    private static string CompletionsUrl(string baseUrl) =>
        $"{baseUrl.TrimEnd('/')}/chat/completions";

    private static string BuildPingBody(string model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteNumber("max_tokens", 1);

            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", "ping");
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildRequestBody(string model, string systemPrompt, string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteBoolean("stream", false);

            writer.WriteStartArray("messages");

            writer.WriteStartObject();
            writer.WriteString("role", "system");
            // Fixed contract + the user's instructions for this mode, composed by AppSettings.
            writer.WriteString("content", systemPrompt);
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

            // Providers can return an error object inside a 200 response.
            if (root.TryGetProperty("error", out var error))
                return CorrectionResult.Error(DescribeErrorObject(error));

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return CorrectionResult.Error("The AI returned an empty response. Try Regenerate.");
            }

            var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            var cleaned = ModelOutput.Clean(content);

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

    private static string DescribeHttpFailure(HttpStatusCode status, string payload, AppSettings settings)
    {
        var detail = TryReadErrorMessage(payload);

        return status switch
        {
            HttpStatusCode.Unauthorized => "Your API key was rejected. Check it in Settings.",
            HttpStatusCode.PaymentRequired => "Your account with this provider is out of credit.",
            // The provider's 404 body is genuinely useful here — OpenRouter names the
            // right slug when a :free model has moved to paid-only.
            HttpStatusCode.NotFound => string.IsNullOrWhiteSpace(detail)
                ? $"Model \"{settings.Model}\" was not found. Pick another in Settings."
                : detail,
            HttpStatusCode.TooManyRequests => DescribeRateLimit(payload, settings),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "The provider timed out. Try Regenerate.",
            >= HttpStatusCode.InternalServerError => "The provider had a server problem. Try Regenerate.",
            _ => string.IsNullOrWhiteSpace(detail) ? $"The provider replied {(int)status}." : detail,
        };
    }

    /// <summary>
    /// A 429 means two very different things, and telling them apart decides what
    /// the user should actually do.
    ///
    /// When <c>limit_source</c> is the upstream provider's shared pool, the free
    /// capacity behind that one model is exhausted for everyone using it. It is not
    /// this account's budget, so a fresh API key changes nothing and neither does
    /// waiting a minute — the pool can stay saturated for hours. Only another model
    /// gets the user moving again, so that is what we tell them.
    /// </summary>
    private static string DescribeRateLimit(string payload, AppSettings settings)
    {
        if (IsUpstreamPoolExhausted(payload))
        {
            return $"\"{settings.Model}\" has no shared upstream capacity right now. " +
                   "This is not your API key — pick a different model in Settings.";
        }

        return settings.Model.EndsWith(":free", StringComparison.OrdinalIgnoreCase)
            ? "Free-model rate limit reached. Wait a minute, or switch to a paid model in Settings."
            : "Rate limited by the provider. Wait a moment and try Regenerate.";
    }

    /// <summary>
    /// Reads <c>error.metadata.limit_source</c>, which only OpenRouter sends. Anything
    /// unreadable or unrecognised is treated as a normal account rate limit, which is
    /// the safer guess: it tells the user to wait rather than to go and change a
    /// setting that was never wrong.
    /// </summary>
    private static bool IsUpstreamPoolExhausted(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("limit_source", out var source)
                && source.ValueKind == JsonValueKind.String
                && source.GetString() is "upstream_provider_shared_pool";
        }
        catch
        {
            return false;
        }
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


    public void Dispose() => _http.Dispose();
}
