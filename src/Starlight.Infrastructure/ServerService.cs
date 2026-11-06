using System.Diagnostics;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class ServerService(PathService paths, Downloads downloads, GameService game, JavaService java)
{
    public async Task<List<LocalServer>> ListAsync(CancellationToken ct)
    {
        var result = new List<LocalServer>();
        foreach (var directory in Directory.EnumerateDirectories(paths.Servers))
            if (await JsonStore.ReadAsync<LocalServer>(Path.Combine(directory, "server.json"), ct) is { } server) result.Add(server);
        return result;
    }
    public async Task<LocalServer> CreateAsync(string name, string version, ServerKind kind, bool acceptedEula, int port, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        if (!acceptedEula) throw new InvalidOperationException("Перед созданием сервера примите Minecraft EULA.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || port is < 1024 or > 65535) throw new ArgumentException("Проверьте имя и порт сервера (1024–65535).");
        var server = new LocalServer(Guid.NewGuid(), name.Trim(), kind == ServerKind.Nukkit ? "Bedrock" : version, kind, port);
        var root = paths.Server(server.Id); Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "world"));
        string url; string? hash; string algorithm = "SHA1";
        if (kind == ServerKind.Vanilla)
        {
            var manifest = await game.ManifestAsync(version, ct);
            if (!manifest.GetProperty("downloads").TryGetProperty("server", out var item)) throw new InvalidOperationException("Для этой версии нет официального сервера.");
            url = item.GetProperty("url").GetString()!; hash = item.GetProperty("sha1").GetString();
        }
        else if (kind == ServerKind.Paper)
        {
            var builds = await downloads.JsonAsync($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds", ct);
            var build = builds.EnumerateArray().FirstOrDefault(b => b.GetProperty("channel").GetString() == "STABLE");
            if (build.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new InvalidOperationException("Для этой версии нет стабильного Paper.");
            var item = build.GetProperty("downloads").GetProperty("server:default");
            url = item.GetProperty("url").GetString()!; hash = item.GetProperty("checksums").GetProperty("sha256").GetString(); algorithm = "SHA256";
        }
        else if (kind == ServerKind.Fabric)
        {
            var loaders = await downloads.JsonAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(version)}", ct);
            var loader = loaders.EnumerateArray().First(x => x.GetProperty("loader").GetProperty("stable").GetBoolean()).GetProperty("loader").GetProperty("version").GetString();
            var installers = await downloads.JsonAsync("https://meta.fabricmc.net/v2/versions/installer", ct);
            var installer = installers.EnumerateArray().First(x => x.GetProperty("stable").GetBoolean()).GetProperty("version").GetString();
            url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(version)}/{loader}/{installer}/server/jar"; hash = null;
        }
        else
        {
            var core = await new ServerCores(downloads).ResolveAsync(kind, version, ct);
            var major = kind == ServerKind.Nukkit ? 21 : await game.JavaMajorAsync(version, ct);
            var arguments = new[] { "-jar", "server.jar", "nogui" };
            if (core.Mode == "jar") await downloads.FileAsync(core.Url, Path.Combine(root, "server.jar"), core.Hash, core.Algorithm, progress, ct);
            else
            {
                var executable = await java.EnsureAsync(major, progress, ct, core.Mode == "spigot");
                var installer = Path.Combine(root, core.Mode == "spigot" ? "BuildTools.jar" : "installer.jar");
                await downloads.FileAsync(core.Url, installer, core.Hash, core.Algorithm, progress, ct);
                if (core.Mode == "spigot")
                {
                    await ServerCores.RunInstallerAsync(executable, root, ["-Xmx2G", "-Duser.home=" + root, "-jar", installer, "--rev", version, "--compile", "SPIGOT", "--output-dir", root, "--final-name", "server.jar"], progress, ct);
                    if (!File.Exists(Path.Combine(root, "server.jar"))) throw new IOException("BuildTools не создал server.jar.");
                }
                else
                {
                    await ServerCores.RunInstallerAsync(executable, root, ["-jar", installer, "--installServer"], progress, ct);
                    var argsName = OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt";
                    var argsFile = Directory.EnumerateFiles(root, argsName, SearchOption.AllDirectories).FirstOrDefault();
                    if (argsFile != null) arguments = ["@" + Path.GetRelativePath(root, argsFile).Replace('\\', '/'), "nogui"];
                    else
                    {
                        var jar = Directory.EnumerateFiles(root, "forge-*.jar").FirstOrDefault(f => !f.EndsWith("-installer.jar")) ?? throw new IOException("Forge не создал файл запуска сервера.");
                        arguments = ["-jar", Path.GetFileName(jar), "nogui"];
                    }
                }
            }
            if (kind == ServerKind.Nukkit) { arguments = ["-jar", "server.jar"]; await File.WriteAllTextAsync(Path.Combine(root, "nukkit.yml"), "settings:\n  language: eng\n", ct); }
            await JsonStore.WriteAsync(Path.Combine(root, "launch.json"), new ServerLaunch(major, arguments, core.Url, core.Build), ct);
            url = ""; hash = null;
        }
        if (url.Length > 0) await downloads.FileAsync(url, Path.Combine(root, "server.jar"), hash, algorithm, progress, ct);
        await File.WriteAllTextAsync(Path.Combine(root, "eula.txt"), $"# Accepted in Starlight at {DateTimeOffset.UtcNow:O}\neula=true\n", ct);
        await ServerProperties.WriteAsync(Path.Combine(root, "server.properties"), new(), port, ct, kind == ServerKind.Nukkit);
        await JsonStore.WriteAsync(Path.Combine(root, "server.json"), server, ct); return server;
    }
    public async Task<Process> StartAsync(LocalServer server, IProgress<TransferProgress> progress, Action<string> log, CancellationToken ct)
    {
        var launch = await JsonStore.ReadAsync<ServerLaunch>(Path.Combine(paths.Server(server.Id), "launch.json"), ct);
        var executable = await java.EnsureAsync(launch?.JavaMajor ?? await game.JavaMajorAsync(server.Version, ct), progress, ct);
        var start = new ProcessStartInfo(executable) { WorkingDirectory = paths.Server(server.Id), RedirectStandardInput = true };
        foreach (var arg in new[] { "-Xms512M", $"-Xmx{server.MemoryMb}M" }.Concat(launch?.Arguments ?? ["-jar", "server.jar", "nogui"])) start.ArgumentList.Add(arg);
        var process = new Process { StartInfo = start }; ProcessLog.Start(process, log); return process;
    }
    public static async Task StopAsync(Process process, CancellationToken ct)
    {
        if (process.HasExited) return;
        await process.StandardInput.WriteLineAsync("stop".AsMemory(), ct); await process.StandardInput.FlushAsync(ct);
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
    }
    public static async Task SendCommandAsync(Process process, string command, CancellationToken ct)
    {
        var value = ServerProperties.Command(command);
        if (process.HasExited) throw new InvalidOperationException("Сервер уже остановлен.");
        await process.StandardInput.WriteLineAsync(value.AsMemory(), ct); await process.StandardInput.FlushAsync(ct);
    }
}
