using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DownholeStudio.App.Services;
using DownholeStudio.App.ViewModels;
using DownholeStudio.App.Views;
using DownholeStudio.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownholeStudio.App;

public partial class App : Application
{
    /// <summary>The service container; tests may replace it before start-up.</summary>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services ??= AppHost.Build();
        Avalonia.Logging.Logger.Sink = new AvaloniaLogForwarder(Services.GetRequiredService<ILoggerFactory>().CreateLogger("Avalonia"));
        var main = Services.GetRequiredService<MainViewModel>();
        var settings = Services.GetRequiredService<SettingsStore>();
        main.ApplyTheme(settings.Current.Theme);
        InstallCrashHandlers(main);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = main };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => main.Shutdown();
            var file = desktop.Args?.FirstOrDefault(a => a.EndsWith(".dhsx", StringComparison.OrdinalIgnoreCase));
            window.Opened += async (_, _) => await main.StartupAsync(file);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The application never disappears silently: errors are logged, a recovery copy is saved and the user is told.</summary>
    private static void InstallCrashHandlers(MainViewModel main)
    {
        var crash = Services!.GetRequiredService<CrashReporter>();
        var log = Services!.GetRequiredService<ILogger<App>>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                log.LogCritical(ex, "Unhandled exception");
                crash.Write(ex, "AppDomain");
                main.EmergencySave();
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            crash.Write(e.Exception, "Task");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.LogError(e.Exception, "UI exception");
            var file = crash.Write(e.Exception, "UI");
            main.EmergencySave();
            e.Handled = true;
            _ = main.Ui.ErrorAsync("Something went wrong",
                "Downhole Studio hit an unexpected error. Your work has been saved to a recovery copy and the application will keep running. If this happens again, use Help → Export diagnostic package and send it to QuakeLogic support.",
                $"{e.Exception.GetType().Name}: {e.Exception.Message}\nCrash report: {file}");
        };
    }
}
