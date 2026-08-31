using System.IO;
using System.Text.Json;
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
                Current.AdoptLegacyProvider();
            }
        }
        catch (Exception ex)
        {
            // A corrupt or hand-mangled file must not stop the app from starting.
            AppLog.Error("settings.json could not be read; falling back to defaults.", ex);
            Current = new AppSettings();
        }

        Current.Normalize();
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Json));
        Current = settings;

        AppLog.Info($"Settings saved. baseUrl={settings.ApiBaseUrl} model={settings.Model} " +
                    $"hotkey={settings.Hotkey} startWithWindows={settings.StartWithWindows} " +
                    $"hasKey={settings.HasApiKey}");
        Changed?.Invoke(settings);
    }
}
