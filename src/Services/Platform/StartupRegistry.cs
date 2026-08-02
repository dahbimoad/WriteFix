using Microsoft.Win32;
using WriteFix.Services.Logging;

namespace WriteFix.Services.Platform;

/// <summary>
/// "Start with Windows", via the current user's Run key. No service, no scheduled
/// task, no admin rights (ARCHITECTURE.md §8).
/// </summary>
public static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WriteFix";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read the Run key.", ex);
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    AppLog.Warn("Start with Windows skipped: executable path is unknown.");
                    return;
                }

                key.SetValue(ValueName, $"\"{exe}\" --background");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            AppLog.Info($"Start with Windows set to {enabled}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not update the Run key.", ex);
        }
    }
}
