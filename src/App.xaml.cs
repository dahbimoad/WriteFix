using System.Windows;
using System.Windows.Threading;
using WriteFix.Interop;
using WriteFix.Services.Logging;
using WriteFix.Services.Platform;
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
            // Running WriteFix again is how people ask for its window back — the tray
            // icon is usually hidden in the Windows 11 overflow flyout. So hand the
            // request to the live instance and leave quietly, rather than scolding.
            if (!startInBackground)
                AppMessageWindow.BroadcastShowSettings();

            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        AppLog.Info("WriteFix starting.");

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;

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
        AppLog.Error("Unhandled exception outside the UI thread.", e.ExceptionObject as Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("WriteFix exiting.");

        _tray?.Dispose();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
