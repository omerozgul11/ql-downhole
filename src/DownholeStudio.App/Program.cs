using Avalonia;

namespace DownholeStudio.App;

internal static class Program
{
    /// <summary>Start-up. Do not touch Avalonia APIs before AppMain runs.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Lets the installer and uninstaller detect a running instance (Inno Setup AppMutex).
        using var running = new Mutex(false, "QuakeLogic.DownholeStudio.Running");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseSkia()
            .LogToTrace();
}
