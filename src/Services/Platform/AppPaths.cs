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

    /// <summary>
    /// An always-empty working folder for the OpenCode server. OpenCode treats its
    /// working folder as a project, and a request made inside a real project folder
    /// was measurably larger in testing, so it never runs anywhere that has files.
    /// </summary>
    public static string OpenCodeWorkspace => Path.Combine(Root, "OpenCode");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(OpenCodeWorkspace);
    }
}
