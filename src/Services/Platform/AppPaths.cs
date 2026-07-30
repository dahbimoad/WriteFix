using System.IO;
namespace WriteFix.Services.Platform;

/// <summary>Every file WriteFix owns lives under %LocalAppData%\WriteFix.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WriteFix");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ApiKeyFile => Path.Combine(Root, "apikey.dat");
    public static string LogDirectory => Path.Combine(Root, "Logs");
    public static string LogFile => Path.Combine(LogDirectory, "writefix.log");
    public static string LogBackupFile => Path.Combine(LogDirectory, "writefix.previous.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
