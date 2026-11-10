using Android.App;
using Android.Content;
using Android.OS;
using Starlight.Domain;
using Starlight.Platform;

namespace Starlight.Droid;

/// <summary>
/// Storage: the app's private internal directory (Context.FilesDir). It is the only place Android allows executable code to live and it
/// needs no runtime permissions. User-visible import/export goes through the Storage Access Framework (Avalonia StorageProvider).
/// </summary>
public sealed class AndroidPlatform : IPlatformServices
{
    private readonly Context context;
    private readonly NativeJvmRuntime? runtime;

    public AndroidPlatform(Context context)
    {
        this.context = context;
        DataRoot = Path.Combine(context.FilesDir!.AbsolutePath, "starlight");
        Directory.CreateDirectory(Path.Combine(DataRoot, "cache"));
        runtime = NativeJvmRuntime.TryCreate();
        var manager = (ActivityManager)context.GetSystemService(Context.ActivityService)!;
        var info = new ActivityManager.MemoryInfo(); manager.GetMemoryInfo(info);
        var abis = Build.SupportedAbis ?? [];
        var arm64 = abis.Contains("arm64-v8a");
        string? blocker = null;
        if (!arm64) blocker = "Устройство не поддерживает arm64-v8a: запуск игры недоступен.";
        else if (runtime == null) blocker = "Нативное ядро запуска (libstarlight_jvm.so) не включено в эту сборку. Управление сборками, модами и настройками работает; запуск игры станет доступен после добавления ядра (см. docs/MOBILE_PORT.md).";
        Device = new DeviceInfo("Android " + Build.VERSION.Release, info.TotalMem / (1024 * 1024), Java.Lang.Runtime.GetRuntime()!.AvailableProcessors(),
            blocker == null, blocker);
        Performance = new AndroidPerformance(context, Path.Combine(DataRoot, "cache", "fps"));
    }

    public string DataRoot { get; }
    public DeviceInfo Device { get; }
    public IPerformanceSource Performance { get; }
    public IGameRuntime? Runtime => Device.CanLaunchGame ? runtime : null;
    public IReadOnlyList<int> JavaMajors { get; } = [8, 17, 21];

    public async Task<string> EnsureJavaAsync(int major, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        // Mojang asks for 8/17/21; map anything else to the closest bundled runtime.
        var bundled = major <= 8 ? 8 : major <= 17 ? 17 : 21;
        var root = await BundledJava.EnsureAsync(Path.Combine(DataRoot, "runtimes"), bundled,
            () => { try { return context.Assets!.Open($"jre/jre-{bundled}-arm64.zip"); } catch (Java.IO.IOException) { return null; } },
            Path.Combine("lib", "server", "libjvm.so"), progress, ct);
        return Path.Combine(root, "bin", "java");
    }

    public Task OpenUrlAsync(string url)
    {
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url)); intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
        return Task.CompletedTask;
    }
}

/// <summary>
/// RAM comes from /proc (works everywhere). Temperatures: thermal zones are blocked by SELinux on many Android 10+ devices, so the battery
/// sensor is used as a fallback for the "CPU" figure. FPS is produced by the native shim (it counts eglSwapBuffers calls inside the game
/// process) and published as a plain number in a file; without the shim the value is simply absent.
/// </summary>
internal sealed class AndroidPerformance(Context context, string fpsFile) : IPerformanceSource
{
    public PerfSnapshot Read()
    {
        var (total, available) = MemInfo();
        return new PerfSnapshot(ReadFps(), Thermal("cpu", "soc", "tsens", "big", "little") ?? BatteryTemp(), Thermal("gpu", "kgsl"),
            Math.Max(0, total - available), total, ProcessRssMb());
    }

    private static (long Total, long Available) MemInfo()
    {
        long total = 0, available = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kb(line) / 1024;
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) { available = Kb(line) / 1024; break; }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (total, available);
    }

    private static long Kb(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }

    private static long ProcessRssMb()
    {
        try
        {
            var line = File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("VmRSS:", StringComparison.Ordinal));
            return line == null ? 0 : Kb(line) / 1024;
        }
        catch (IOException) { return 0; }
    }

    private static double? Thermal(params string[] names)
    {
        try
        {
            const string root = "/sys/class/thermal";
            if (!Directory.Exists(root)) return null;
            double? best = null;
            foreach (var zone in Directory.EnumerateDirectories(root, "thermal_zone*"))
            {
                var type = File.ReadAllText(Path.Combine(zone, "type")).Trim().ToLowerInvariant();
                if (!names.Any(type.Contains)) continue;
                if (!double.TryParse(File.ReadAllText(Path.Combine(zone, "temp")).Trim(), out var raw)) continue;
                var celsius = raw > 1000 ? raw / 1000 : raw;
                if (celsius is > 0 and < 150 && (best == null || celsius > best)) best = celsius;
            }
            return best;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private double? BatteryTemp()
    {
        using var filter = new IntentFilter(Intent.ActionBatteryChanged);
        var intent = context.RegisterReceiver(null, filter);
        var tenths = intent?.GetIntExtra(BatteryManager.ExtraTemperature, int.MinValue) ?? int.MinValue;
        return tenths == int.MinValue ? null : tenths / 10.0;
    }

    private double? ReadFps()
    {
        try
        {
            var info = new FileInfo(fpsFile);
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(3)) return null; // stale = game not running
            return double.TryParse(File.ReadAllText(fpsFile).Trim(), System.Globalization.CultureInfo.InvariantCulture, out var fps) ? fps : null;
        }
        catch (IOException) { return null; }
    }
}
