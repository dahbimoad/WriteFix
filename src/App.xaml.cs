using System.Windows;
using System.Windows.Threading;
using WriteFix.Interop;
using WriteFix.Services.Logging;
using WriteFix.Services.Platform;
using WriteFix.Services.Updates;
using Application = System.Windows.Application;

namespace WriteFix;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\WriteFix.SingleInstance";
    private const string BackgroundArgument = "--background";

    private Mutex? _instanceMutex;
    private TrayApp? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startInBackground = e.Args.Contains(BackgroundArgument, StringComparer.OrdinalIgnoreCase);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            AppLog.Info($"Second instance detected; handing over to the running one. background={startInBackground}");

            // Running WriteFix again is how people ask for its window back — the tray
            // icon is usually hidden in the Windows 11 overflow flyout. So hand the
            // request to the live instance and leave quietly, rather than scolding.
            if (!startInBackground)
                AppMessageWindow.BroadcastShowSettings();

            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        AppLog.Info($"WriteFix starting. version={UpdateService.CurrentVersion} os={Environment.OSVersion.VersionString} " +
                    $"runtime={Environment.Version} background={startInBackground} pid={Environment.ProcessId}");

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _tray = new TrayApp();

        if (startInBackground)
            AppLog.Info("WriteFix started quietly in the background.");
        else
            _tray.OpenSettings();
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray process alive; a single failed operation is recoverable.
        AppLog.Error("Unhandled exception on the UI thread.", e.Exception);
        e.Handled = true;
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error($"Unhandled exception outside the UI thread. terminating={e.IsTerminating}", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("A background task failed and nobody observed it.", e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"WriteFix exiting. exitCode={e.ApplicationExitCode}");

        _tray?.Dispose();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
