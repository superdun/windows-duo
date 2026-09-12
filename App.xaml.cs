using System.Windows;
using System.Windows.Threading;

namespace WindowsDuo;

public partial class App : System.Windows.Application
{
    private TrayIcon? _tray;
    private MainWindow? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Log.StartSession();
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        Log.Info("app start tray");
        base.OnStartup(e);

        _settings = new MainWindow();
        _settings.Opacity = 0;
        _settings.Show();
        _settings.Hide();
        _settings.Opacity = 1;
        _tray = new TrayIcon(_settings);
        _settings.TrayDisposed += () =>
        {
            _tray?.Dispose();
            _tray = null;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("dispatcher unhandled", e.Exception);
        e.Handled = true;
    }

    private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Error("domain unhandled", ex);
        }
        else
        {
            Log.Error($"domain unhandled: {e.ExceptionObject}");
        }
    }

    private static void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("task unobserved", e.Exception);
        e.SetObserved();
    }
}
