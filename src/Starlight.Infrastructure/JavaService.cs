using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class JavaService(PathService paths, Downloads downloads)
{
    private readonly SemaphoreSlim gate = new(1);
    public async Task<string> EnsureAsync(int major, IProgress<TransferProgress>? progress, CancellationToken ct, bool jdk = false)
    {
        await gate.WaitAsync(ct);
        try
        {
            var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
            var arch = RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "aarch64", _ => throw new PlatformNotSupportedException("Поддерживаются x64 и ARM64.") };
            var root = Path.Combine(paths.Runtimes, $"{(jdk ? "jdk" : "java")}-{major}-{os}-{arch}");
            var marker = Path.Combine(root, "ready.json");
            if (await JsonStore.ReadAsync<string>(marker, ct) is { } installed && File.Exists(Path.Combine(root, installed))) return Path.Combine(root, installed);
            var assets = await downloads.JsonAsync($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture={arch}&image_type={(jdk ? "jdk" : "jre")}&os={os}&vendor=eclipse", ct);
            if (assets.GetArrayLength() == 0) throw new InvalidOperationException($"JRE {major} для {os}/{arch} недоступен.");
            var package = assets[0].GetProperty("binary").GetProperty("package");
            var archive = PathService.SafeChild(paths.Cache, package.GetProperty("name").GetString()!);
            await downloads.FileAsync(package.GetProperty("link").GetString()!, archive, package.GetProperty("checksum").GetString(), "SHA256", progress, ct);
            Directory.CreateDirectory(root);
            await Task.Run(() =>
            {
                if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(archive, root, true);
                else
                {
                    using var file = File.OpenRead(archive); using var gzip = new GZipStream(file, CompressionMode.Decompress);
                    TarFile.ExtractToDirectory(gzip, root, true);
                }
            }, ct);
            var java = Directory.EnumerateFiles(root, OperatingSystem.IsWindows() ? "java.exe" : "java", SearchOption.AllDirectories)
                .FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p)) == "bin") ?? throw new InvalidDataException("В архиве не найден Java.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(java, File.GetUnixFileMode(java) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            await JsonStore.WriteAsync(marker, Path.GetRelativePath(root, java), ct);
            return java;
        }
        finally { gate.Release(); }
    }
}
