using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class UpdateService(HttpClient http, PathService paths)
{
    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
    public SignedRelease? Envelope { get; private set; }
    public ReleaseManifest? Available { get; private set; }
    public async Task<ReleaseManifest?> CheckAsync(string feed, CancellationToken ct)
    {
        var uri = ApiClient.ServiceUri(feed);
        // ServiceUri normalizes base paths; a feed is a file, so preserve the original URL.
        _ = uri;
        var json = await new ApiClient(http).GetAsync(feed.Trim(), ct);
        var envelope = json.Deserialize<SignedRelease>(JsonStore.Options) ?? throw new InvalidDataException("Некорректная лента обновлений.");
        var release = ReleaseTrust.Verify(envelope);
        if (Version.Parse(release.Version) <= Version.Parse(CurrentVersion)) { Available = null; Envelope = null; return null; }
        if (!release.Artifacts.Any(a => a.Runtime == RuntimeInformation.RuntimeIdentifier)) throw new InvalidOperationException("В этом выпуске нет пакета для вашей системы.");
        Envelope = envelope; Available = release; return release;
    }
    public async Task<string> PrepareAsync(string installation, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var release = Available ?? throw new InvalidOperationException("Сначала проверьте обновления.");
        var artifact = release.Artifacts.Single(a => a.Runtime == RuntimeInformation.RuntimeIdentifier);
        _ = ApiClient.ServiceUri(artifact.Url);
        var staging = Path.Combine(paths.Cache, "updates", release.Version + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var package = Path.Combine(staging, "package.zip");
        using var response = await http.GetAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920]; long received = 0; int count;
            while ((count = await source.ReadAsync(buffer, ct)) > 0)
            {
                received += count; if (received > artifact.Size) throw new InvalidDataException("Размер обновления не совпадает с манифестом.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct); progress.Report(new("Обновление " + release.Version, received * 100d / artifact.Size));
            }
            if (received != artifact.Size) throw new InvalidDataException("Обновление загружено не полностью.");
        }
        if (!await Downloads.VerifyAsync(package, artifact.Sha256, "SHA256", ct)) throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
        var helperName = OperatingSystem.IsWindows() ? "Starlight.Updater.exe" : "Starlight.Updater";
        var helperSource = Path.Combine(installation, "updater");
        if (!File.Exists(Path.Combine(helperSource, helperName))) throw new InvalidOperationException("Не найден модуль обновления. Установите выпуск 1.1 один раз.");
        var helper = Path.Combine(staging, "helper");
        foreach (var file in Directory.EnumerateFiles(helperSource, "*", SearchOption.AllDirectories))
        {
            var target = SafeArchive.Child(helper, Path.GetRelativePath(helperSource, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
        var envelopePath = Path.Combine(staging, "release.json"); await JsonStore.WriteAsync(envelopePath, Envelope!, ct);
        var requestPath = Path.Combine(staging, "request.json");
        await JsonStore.WriteAsync(requestPath, new UpdateRequest(package, envelopePath, Path.GetFullPath(installation), Environment.ProcessId, RuntimeInformation.RuntimeIdentifier), ct);
        return requestPath;
    }
    public static void StartHelper(string requestPath)
    {
        var helper = Path.Combine(Path.GetDirectoryName(requestPath)!, "helper", OperatingSystem.IsWindows() ? "Starlight.Updater.exe" : "Starlight.Updater");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
        start.ArgumentList.Add(requestPath); Process.Start(start)?.Dispose();
    }
}
