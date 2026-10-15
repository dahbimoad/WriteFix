using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteFix.Models;
using WriteFix.Services.Logging;
using WriteFix.Services.Platform;

namespace WriteFix.Services.Settings;

/// <summary>Loads and saves settings.json, and keeps the in-memory copy the app reads.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Enums as names, so settings.json stays readable and hand-editable.
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised after a successful save so the hotkey can be re-registered.</summary>
    public event Action<AppSettings>? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();

                // The file predates multi-provider support if it carries no base URL,
                // so keep that install on OpenRouter rather than pointing an existing
                // OpenRouter key at the new Groq default.
                if (string.IsNullOrWhiteSpace(Current.ApiBaseUrl))
                    AppLog.Info("settings.json has no base URL; keeping this older install on OpenRouter.");
                Current.AdoptLegacyProvider();
            }
            else
            {
                AppLog.Info("No settings.json yet; using defaults.");
            }
        }
        catch (Exception ex)
        {
            // A corrupt or hand-mangled file must not stop the app from starting.
            AppLog.Error("settings.json could not be read; falling back to defaults.", ex);
            Current = new AppSettings();
        }

        Current.Normalize();

        AppLog.Info($"Settings loaded. provider={Current.Provider} baseUrl={Current.ApiBaseUrl} model={Current.Model} " +
                    $"openCodeModel={Current.OpenCodeModel} hotkey={Current.Hotkey} rephraseHotkey={Current.RephraseHotkey} " +
                    $"lockedMode={Current.LockedMode?.ToString() ?? "none"} startWithWindows={Current.StartWithWindows} " +
                    $"autoCheckUpdates={Current.AutoCheckUpdates} hasKey={Current.HasApiKey} " +
                    $"timeoutS={Current.RequestTimeoutSeconds} maxChars={Current.MaxInputCharacters}");
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();

        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception ex)
        {
            AppLog.Error("settings.json could not be written.", ex);
            throw;
        }

        Current = settings;

        AppLog.Info($"Settings saved. provider={settings.Provider} baseUrl={settings.ApiBaseUrl} model={settings.Model} " +
                    $"openCodeModel={settings.OpenCodeModel} hotkey={settings.Hotkey} rephraseHotkey={settings.RephraseHotkey} " +
                    $"lockedMode={settings.LockedMode?.ToString() ?? "none"} startWithWindows={settings.StartWithWindows} " +
                    $"autoCheckUpdates={settings.AutoCheckUpdates} hasKey={settings.HasApiKey}");
        Changed?.Invoke(settings);
    }
}
