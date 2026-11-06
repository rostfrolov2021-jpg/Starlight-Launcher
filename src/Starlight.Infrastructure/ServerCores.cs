using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed record CoreDownload(string Url, string? Hash, string Algorithm, string Build, string Mode = "jar");
public sealed class ServerCores(Downloads downloads)
{
    public async Task<CoreDownload> ResolveAsync(ServerKind kind, string version, CancellationToken ct)
    {
        var v = Uri.EscapeDataString(version);
        switch (kind)
        {
            case ServerKind.Spigot:
                return new("https://hub.spigotmc.org/jenkins/job/BuildTools/lastSuccessfulBuild/artifact/target/BuildTools.jar", null, "SHA256", version, "spigot");
            case ServerKind.Forge:
                var promos = (await downloads.JsonAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct)).GetProperty("promos");
                if (!promos.TryGetProperty(version + "-recommended", out var forge) && !promos.TryGetProperty(version + "-latest", out forge)) throw new InvalidOperationException("Forge для этой версии Minecraft не опубликован. Выберите другую версию.");
                var build = version + "-" + forge.GetString();
                var forgeUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{build}/forge-{build}-installer.jar";
                return new(forgeUrl, (await downloads.Http.GetStringAsync(forgeUrl + ".sha1", ct)).Trim().Split(' ')[0], "SHA1", build, "forge");
            case ServerKind.Nukkit:
                const string maven = "https://repo.opencollab.dev/maven-snapshots/cn/nukkit/nukkit/1.0-SNAPSHOT/";
                var xml = XDocument.Parse(await downloads.Http.GetStringAsync(maven + "maven-metadata.xml", ct));
                var snapshot = xml.Descendants("snapshotVersion").FirstOrDefault(x => (string?)x.Element("extension") == "jar" && x.Element("classifier") == null)?.Element("value")?.Value ?? throw new InvalidDataException("Нет опубликованного Nukkit JAR.");
                var nukkitUrl = maven + "nukkit-" + snapshot + ".jar";
                return new(nukkitUrl, (await downloads.Http.GetStringAsync(nukkitUrl + ".sha1", ct)).Trim().Split(' ')[0], "SHA1", snapshot);
            case ServerKind.Arclight:
                var releases = await downloads.JsonAsync("https://api.github.com/repos/IzzelAliz/Arclight/releases?per_page=100", ct);
                foreach (var release in releases.EnumerateArray())
                {
                    if (release.GetProperty("draft").GetBoolean()) continue;
                    var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString()!.Contains("-" + version + "-", StringComparison.Ordinal) && a.GetProperty("name").GetString()!.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.GetProperty("name").GetString()!.Contains("-forge-") ? 0 : a.GetProperty("name").GetString()!.Contains("-neoforge-") ? 1 : 2).ToArray();
                    if (assets.Length == 0) continue;
                    var asset = assets[0]; var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    return new(asset.GetProperty("browser_download_url").GetString()!, digest?.StartsWith("sha256:") == true ? digest[7..] : null, "SHA256", asset.GetProperty("name").GetString()!);
                }
                throw new InvalidOperationException("Arclight для выбранной версии не найден. Попробуйте 1.20.1 или 1.21.1; новые ветки могут быть экспериментальными.");
            case ServerKind.Youer:
                var builds = await downloads.JsonAsync($"https://api.mohistmc.com/project/youer/{v}/builds", ct);
                if (builds.ValueKind != JsonValueKind.Array || builds.GetArrayLength() == 0) throw new InvalidOperationException("Youer для этой версии не опубликован. Попробуйте 1.21.1.");
                var latest = builds.EnumerateArray().OrderByDescending(x => x.GetProperty("id").GetInt32()).First();
                var id = latest.GetProperty("id").GetInt32();
                return new($"https://api.mohistmc.com/project/youer/{v}/builds/{id}/download", latest.GetProperty("file_sha256").GetString(), "SHA256", id.ToString());
            default: throw new ArgumentException("Это ядро устанавливается основным загрузчиком.");
        }
    }
    public static async Task RunInstallerAsync(string java, string root, IEnumerable<string> arguments, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var start = new ProcessStartInfo(java) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        // BuildTools and its Git subprocesses keep configuration within this installation.
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(root, "buildtools.gitconfig");
        start.Environment["JAVA_HOME"] = Path.GetDirectoryName(Path.GetDirectoryName(java))!;
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить установщик ядра.");
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync(ct) is { } line) progress.Report(new(line.Length > 220 ? line[..220] : line, 50)); }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        try { await process.WaitForExitAsync(ct); await Task.WhenAll(stdout, stderr); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { } throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Установщик ядра завершился с кодом {process.ExitCode}. Для Spigot необходим Git; диагностические файлы остались в папке установки.");
    }
}
