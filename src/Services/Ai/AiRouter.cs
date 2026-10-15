using WriteFix.Models;
using WriteFix.Services.Logging;
using WriteFix.Services.Settings;

namespace WriteFix.Services.Ai;

/// <summary>
/// Owns both AI back ends and sends each correction to the one chosen in Settings at
/// the moment of the request, so switching needs no restart.
/// </summary>
public sealed class AiRouter : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly OpenCodeServer _openCodeServer = new();

    public AiRouter(SettingsStore settings, SecretStore secrets)
    {
        _settings = settings;
        ChatCompletions = new ChatCompletionsClient(settings, secrets);
        OpenCode = new OpenCodeClient(_openCodeServer, settings);
    }

    public ChatCompletionsClient ChatCompletions { get; }

    public OpenCodeClient OpenCode { get; }

    public Task<CorrectionResult> CorrectAsync(string text, CorrectionMode mode, CancellationToken cancellationToken)
    {
        var provider = _settings.Current.Provider;
        AppLog.Info($"Routing correction. provider={provider} mode={mode} chars={text.Length}");

        return provider == AiProvider.OpenCode
            ? OpenCode.CorrectAsync(text, mode, cancellationToken)
            : ChatCompletions.CorrectAsync(text, mode, cancellationToken);
    }

    public void Dispose()
    {
        AppLog.Info("AI router disposing.");
        ChatCompletions.Dispose();
        _openCodeServer.Dispose();
    }
}
