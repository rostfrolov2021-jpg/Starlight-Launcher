using System.IO.Compression;
using Starlight.Domain;

namespace Starlight.Platform;

/// <summary>
/// Unpacks a JRE that ships inside the app (Android APK asset) into the private data directory exactly once.
/// Streams entry by entry with a 64 KB buffer, so peak memory stays flat regardless of the JRE size.
/// Expected layout inside the zip: a normal JRE tree (bin/, lib/, ...). Repackage .tar.xz runtimes as .zip first (see docs).
/// </summary>
public static class BundledJava
{
    public static async Task<string> EnsureAsync(string runtimesRoot, int major, Func<Stream?> openArchive, string libJvmRelative,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var root = Path.Combine(runtimesRoot, $"jre-{major}-android-arm64");
        var marker = Path.Combine(root, ".ready");
        if (File.Exists(marker) && File.Exists(Path.Combine(root, libJvmRelative))) return root;
        await using var source = openArchive() ?? throw new FileNotFoundException($"JRE {major} не включена в приложение.");
        var staging = root + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        using (var zip = new ZipArchive(source, ZipArchiveMode.Read))
        {
            var done = 0; var count = Math.Max(1, zip.Entries.Count);
            var buffer = new byte[64 * 1024];
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) { done++; continue; }
                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("Небезопасный путь в архиве JRE.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = entry.Open())
                await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0) await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (!OperatingSystem.IsWindows() && target.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute);
                progress?.Report(new($"JRE {major}", ++done * 100d / count));
            }
        }
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.Move(staging, root);
        await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"), ct);
        return root;
    }
}
