using Foundation;
using Starlight.Domain;
using Starlight.Platform;
using UIKit;

namespace Starlight.iOS;

/// <summary>
/// iOS sandbox: data lives in Library/Application Support (private to the app, not user-visible; import/export through the document picker).
/// iOS cannot run Minecraft: Java Edition. There is no JIT for third-party apps, no fork/exec, and no JVM for the platform — App Store rules
/// and the OS itself forbid it. The iOS app is therefore a companion: build and manage instances, download mods/resource packs/shaders,
/// import modpacks, edit settings. Runtime is null and the UI says so.
/// </summary>
public sealed class IosPlatform : IPlatformServices
{
    public IosPlatform()
    {
        DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "starlight");
        Directory.CreateDirectory(DataRoot);
        Device = new DeviceInfo("iOS " + UIDevice.CurrentDevice.SystemVersion, (long)(NSProcessInfo.ProcessInfo.PhysicalMemory / (1024 * 1024)),
            (int)NSProcessInfo.ProcessInfo.ProcessorCount, false,
            "На iOS запуск Minecraft: Java Edition невозможен (нет JIT и JVM для приложений). Здесь можно собирать сборки, ставить моды, текстурпаки и шейдеры; играть — на ПК или Android.");
        Performance = new IosPerformance();
    }

    public string DataRoot { get; }
    public DeviceInfo Device { get; }
    public IPerformanceSource Performance { get; }
    public IGameRuntime? Runtime => null;
    public IReadOnlyList<int> JavaMajors { get; } = [8, 17, 21];
    public Task<string> EnsureJavaAsync(int major, IProgress<TransferProgress>? progress, CancellationToken ct)
        => throw new PlatformNotSupportedException("Java недоступна на iOS.");

    public Task OpenUrlAsync(string url)
    {
        var tcs = new TaskCompletionSource();
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            UIApplication.SharedApplication.OpenUrl(new NSUrl(url), new UIApplicationOpenUrlOptions(), _ => tcs.TrySetResult());
        });
        return tcs.Task;
    }
}

/// <summary>iOS exposes no temperatures to apps — only the coarse thermal state — and no per-process FPS for other processes.</summary>
internal sealed class IosPerformance : IPerformanceSource
{
    public PerfSnapshot Read()
    {
        var total = (long)(NSProcessInfo.ProcessInfo.PhysicalMemory / (1024 * 1024));
        var process = Environment.WorkingSet / (1024 * 1024);
        var thermal = NSProcessInfo.ProcessInfo.ThermalState switch
        {
            NSProcessInfoThermalState.Nominal => "Темп. норм.",
            NSProcessInfoThermalState.Fair => "Темп. тепло",
            NSProcessInfoThermalState.Serious => "Темп. высокая",
            NSProcessInfoThermalState.Critical => "Темп. критич.",
            _ => null
        };
        return new PerfSnapshot(null, null, null, process, total, process, thermal);
    }
}
