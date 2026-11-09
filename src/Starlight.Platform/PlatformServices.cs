using Starlight.Domain;

namespace Starlight.Platform;

/// <summary>One sample for the in-game / in-launcher performance overlay. Every sensor is optional: Android and iOS expose different things.</summary>
public sealed record PerfSnapshot(double? Fps, double? CpuTempC, double? GpuTempC, long RamUsedMb, long RamTotalMb,
    long ProcessMb, string? Thermal = null);

public interface IPerformanceSource
{
    PerfSnapshot Read();
}

public sealed record DeviceInfo(string Platform, long TotalRamMb, int CpuCores, bool CanLaunchGame, string? LaunchBlocker);

/// <summary>Starts the Java process described by a <see cref="LaunchPlan"/>. Returns the game's exit code.</summary>
public interface IGameRuntime
{
    Task<int> RunAsync(LaunchPlan plan, Action<string> log, CancellationToken ct);
}

/// <summary>Everything the shared mobile UI and core need from the host OS.</summary>
public interface IPlatformServices
{
    /// <summary>Private, app-owned directory for instances, runtimes and caches (PathService root).</summary>
    string DataRoot { get; }
    DeviceInfo Device { get; }
    IPerformanceSource Performance { get; }
    /// <summary>Null when the platform cannot run the game (iOS, or Android without the native launcher library).</summary>
    IGameRuntime? Runtime { get; }
    /// <summary>Java majors this platform can run (empty means "whatever the version manifest asks for").</summary>
    IReadOnlyList<int> JavaMajors { get; }
    /// <summary>Provides the JRE for a given major version (bundled in the APK on Android).</summary>
    Task<string> EnsureJavaAsync(int major, IProgress<TransferProgress>? progress, CancellationToken ct);
    Task OpenUrlAsync(string url);
}

public static class MobileTuning
{
    /// <summary>Installer parallelism by device RAM. Each worker keeps buffers + hashing state; 8x6 workers OOM-kill low-end phones.</summary>
    public static InstallTuning For(long totalRamMb) => totalRamMb switch
    {
        < 3072 => new(2, 2, 64),
        < 6144 => new(3, 4, 128),
        _ => new(4, 6, 192)
    };

    /// <summary>Upper bound for the game heap slider: the OS and the launcher still need room, Android's low-memory killer is merciless.</summary>
    public static int MaxHeapMb(long totalRamMb)
    {
        var cap = (int)Math.Min(totalRamMb * 55 / 100, 8192);
        return Math.Max(1024, cap / 256 * 256);
    }
}
