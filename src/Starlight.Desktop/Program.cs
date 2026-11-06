using Avalonia;
using Avalonia.ReactiveUI;
using Serilog;
using Starlight.Infrastructure;

namespace Starlight.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var paths = new PathService(Environment.GetEnvironmentVariable("STARLIGHT_HOME"));
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.File(Path.Combine(paths.Logs, "launcher.log"),
            fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true, retainedFileCountLimit: 5, shared: true).CreateLogger();
        try { Log.Information("Starting Starlight {Version}", UpdateService.CurrentVersion); BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception e) { Log.Fatal(e, "Launcher failed"); throw; }
        finally { Log.Information("Starlight stopped"); Log.CloseAndFlush(); }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace().UseReactiveUI();
}
