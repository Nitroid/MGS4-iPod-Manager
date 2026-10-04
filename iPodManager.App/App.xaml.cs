using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Threading;

namespace iPodManager;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\iPodManager.Production.SingleInstance";
    private SingleInstanceGuard? _singleInstance;

    internal static Stopwatch StartupStopwatch { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupStopwatch.Restart();
        base.OnStartup(e);
        try
        {
            _singleInstance = SingleInstanceGuard.TryAcquire(SingleInstanceMutexName);
        }
        catch (Exception)
        {
            MessageBox.Show(
                "iPod Manager could not start. Please try again.",
                "iPod Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (_singleInstance is null)
        {
            MessageBox.Show(
                "iPod Manager is already running.",
                "iPod Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        AppLog.Start();
        StartupLocationResult location;
        try
        {
            AppLog.Info($"startup.location process_executable='{Environment.ProcessPath}' base_directory='{AppContext.BaseDirectory}' current_directory='{Environment.CurrentDirectory}'");
            location = GamePaths.ValidateStartup(AppContext.BaseDirectory, AppLog.Info);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or SecurityException)
        {
            location = new(null, StartupLocationFailure.VerificationError, exception);
        }
        if (location.Failure != StartupLocationFailure.Valid)
        {
            if (location.Error != null)
                AppLog.Error($"startup.location failure={location.Failure}", location.Error);
            else
                AppLog.Warn($"startup.location failure={location.Failure}");
            MessageBox.Show(location.Message, "iPod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        AppLog.Info($"Application started. Version {typeof(App).Assembly.GetName().Version}. thread={Environment.CurrentManagedThreadId}");
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception; the application must close.", e.Exception);
        const string message = "The app encountered an unexpected error and must close. Details are in iPodManager.log.";
        try
        {
            if (MainWindow is MainWindow window && window.IsLoaded && window.IsVisible)
            {
                window.ShowFatalError(message);
                e.Handled = true; // Dismissing the input-blocking overlay shuts the app down.
                return;
            }

            MessageBox.Show(message, "iPod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception displayError)
        {
            AppLog.Error("Could not present the fatal error message.", displayError);
        }

        Shutdown(-1);
        e.Handled = true;
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Category refresh currently starts a task without awaiting it.
        AppLog.Error("Unobserved background task failed.", e.Exception);
        e.SetObserved();
    }
}
