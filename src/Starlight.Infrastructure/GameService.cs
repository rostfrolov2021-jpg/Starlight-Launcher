using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installers;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.ProcessBuilder;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class GameService(PathService paths, Downloads downloads, IJavaProvider java, InstanceService instances)
{
    /// <summary>Installer parallelism. Desktop default; mobile heads lower it to fit the device RAM.</summary>
    public InstallTuning Tuning { get; init; } = new(8, 6, 256);
    public static MSession CreateSession(Account account)
    {
        var session = account.IsMicrosoft
            ? new MSession { Username = account.Name, AccessToken = account.AccessToken, UserType = "msa", Xuid = account.Xuid }
            : MSession.CreateOfflineSession(account.Name);
        // CmlLib generates a random offline UUID by default. Preserve the profile's stable Java UUID.
        session.UUID = account.Uuid;
        return session;
    }
    public async Task<List<GameVersion>> VersionsAsync(CancellationToken ct)
    {
        var cache = Path.Combine(paths.Cache, "versions.json");
        try
        {
            var json = await downloads.JsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
            var result = json.GetProperty("versions").EnumerateArray().Select(v => new GameVersion(v.GetProperty("id").GetString()!, v.GetProperty("type").GetString()!, v.GetProperty("url").GetString()!)).ToList();
            await JsonStore.WriteAsync(cache, result, ct); return result;
        }
        catch (HttpRequestException) when (File.Exists(cache)) { return await JsonStore.ReadAsync<List<GameVersion>>(cache, ct) ?? []; }
    }
    public async Task<JsonElement> ManifestAsync(string version, CancellationToken ct)
    {
        var file = Path.Combine(paths.Cache, "manifest-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(version))) + ".json");
        if (File.Exists(file)) return await JsonStore.ReadAsync<JsonElement>(file, ct);
        var info = (await VersionsAsync(ct)).FirstOrDefault(v => v.Id == version) ?? throw new InvalidOperationException("Версия Minecraft не найдена.");
        var manifest = await downloads.JsonAsync(info.Url, ct); await JsonStore.WriteAsync(file, manifest, ct); return manifest;
    }
    public async Task<int> JavaMajorAsync(string version, CancellationToken ct)
    {
        var manifest = await ManifestAsync(version, ct);
        return manifest.TryGetProperty("javaVersion", out var value) ? value.GetProperty("majorVersion").GetInt32() : 8;
    }
    public async Task<Process> LaunchAsync(Instance instance, Account account, IProgress<TransferProgress> progress, Action<string> log, CancellationToken ct, int? serverPort = null)
    {
        var process = await BuildProcessAsync(instance, account, progress, ct, serverPort);
        ProcessLog.Start(process, log);
        return process;
    }
    /// <summary>Installs everything and returns the exact command line without starting a process (mobile runtimes launch the JVM in-process).</summary>
    public async Task<LaunchPlan> PrepareLaunchAsync(Instance instance, Account account, IProgress<TransferProgress> progress, CancellationToken ct, int? serverPort = null)
    {
        using var process = await BuildProcessAsync(instance, account, progress, ct, serverPort);
        var info = process.StartInfo;
        return new LaunchPlan(info.FileName, info.ArgumentList.ToArray(), info.WorkingDirectory);
    }
    private async Task<Process> BuildProcessAsync(Instance instance, Account account, IProgress<TransferProgress> progress, CancellationToken ct, int? serverPort)
    {
        var javaPath = await java.EnsureAsync(instance.JavaMajor ?? await JavaMajorAsync(instance.MinecraftVersion, ct), progress, ct);
        var path = new MinecraftPath(paths.Game(instance.Id)) { Runtime = paths.Runtimes };
        var parameters = MinecraftLauncherParameters.CreateDefault(path);
        var extractors = DefaultFileExtractors.CreateDefault(parameters.HttpClient, parameters.RulesEvaluator!, parameters.JavaPathResolver!);
        extractors.Java = null;
        parameters.FileExtractors = extractors.ToExtractorCollection();
        parameters.GameInstaller = new ParallelGameInstaller(Tuning.Checkers, Tuning.Downloaders, Tuning.Capacity, parameters.HttpClient) { CheckFileChecksum = true, CheckFileSize = true };
        var launcher = new MinecraftLauncher(parameters);
        launcher.FileProgressChanged += (_, e) => progress.Report(new(e.Name ?? "Minecraft", e.TotalTasks == 0 ? 0 : 100d * e.ProgressedTasks / e.TotalTasks));
        var version = instance.InstalledVersion;
        if (version == null)
        {
            version = await InstallLoaderAsync(instance, launcher, javaPath, ct);
            instance = instance with { InstalledVersion = version }; await instances.SaveAsync(instance, ct);
        }
        await launcher.InstallAsync(version, ct);
        var session = CreateSession(account);
        var option = new MLaunchOption { Session = session, JavaPath = javaPath, MaximumRamMb = instance.MemoryMb, MinimumRamMb = Math.Min(512, instance.MemoryMb), GameLauncherName = "Starlight", GameLauncherVersion = "1.0.0" };
        if (serverPort.HasValue) { option.ServerIp = "127.0.0.1"; option.ServerPort = serverPort.Value; }
        if (!string.IsNullOrWhiteSpace(instance.JvmArgs)) option.ExtraJvmArguments = [MArgument.FromCommandLine(instance.JvmArgs.Trim())];
        var process = await launcher.BuildProcessAsync(version, option, ct);
        process.StartInfo.WorkingDirectory = paths.Game(instance.Id);
        return process;
    }
    private async Task<string> InstallLoaderAsync(Instance instance, MinecraftLauncher launcher, string javaPath, CancellationToken ct)
    {
        if (instance.Loader == ModLoader.Vanilla) return instance.MinecraftVersion;
        var mc = Uri.EscapeDataString(instance.MinecraftVersion);
        if (instance.Loader == ModLoader.Fabric)
        {
            var loaders = await downloads.JsonAsync($"https://meta.fabricmc.net/v2/versions/loader/{mc}", ct);
            var loader = instance.LoaderVersion ?? loaders.EnumerateArray().First(v => v.GetProperty("loader").GetProperty("stable").GetBoolean()).GetProperty("loader").GetProperty("version").GetString()!;
            var profile = await downloads.JsonAsync($"https://meta.fabricmc.net/v2/versions/loader/{mc}/{Uri.EscapeDataString(loader)}/profile/json", ct);
            var id = profile.GetProperty("id").GetString()!;
            var folder = PathService.SafeChild(Path.Combine(paths.Game(instance.Id), "versions"), id);
            await JsonStore.WriteAsync(PathService.SafeChild(folder, id + ".json"), profile, ct); return id;
        }
        if (instance.Loader == ModLoader.Forge)
        {
            var installer = new ForgeInstaller(launcher);
            var options = new ForgeInstallOptions { JavaPath = javaPath, CancellationToken = ct };
            return instance.LoaderVersion == null ? await installer.Install(instance.MinecraftVersion, options)
                : await installer.Install(instance.MinecraftVersion, instance.LoaderVersion, options);
        }
        // NeoForge's installer writes a standard launcher profile into the isolated game directory.
        var xml = XDocument.Parse(await downloads.Http.GetStringAsync("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct));
        var parts = instance.MinecraftVersion.Split('.');
        if (instance.LoaderVersion == null && (parts.Length < 2 || parts[0] != "1")) throw new InvalidOperationException("Версия NeoForge должна быть указана для этой версии Minecraft.");
        var prefix = parts.Length >= 2 ? parts[1] + "." + (parts.Length > 2 ? parts[2] : "0") + "." : "";
        var neo = instance.LoaderVersion ?? xml.Descendants("version").Select(x => x.Value).LastOrDefault(v => v.StartsWith(prefix, StringComparison.Ordinal) && !v.Contains("beta"))
            ?? throw new InvalidOperationException("Стабильный NeoForge для этой версии Minecraft не найден.");
        var url = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neo}/neoforge-{neo}-installer.jar";
        var hash = (await downloads.Http.GetStringAsync(url + ".sha1", ct)).Trim().Split(' ')[0];
        var jar = PathService.SafeChild(paths.Cache, $"neoforge-{neo}-installer.jar");
        await downloads.FileAsync(url, jar, hash, "SHA1", null, ct);
        await launcher.InstallAsync(instance.MinecraftVersion, ct);
        await JsonStore.WriteAsync(Path.Combine(paths.Game(instance.Id), "launcher_profiles.json"), new { profiles = new { } }, ct);
        var start = new ProcessStartInfo(javaPath) { WorkingDirectory = paths.Game(instance.Id), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-jar", jar, "--installClient", paths.Game(instance.Id) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить NeoForge installer.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var registration = ct.Register(() => { if (!process.HasExited) process.Kill(true); });
        await process.WaitForExitAsync(ct); await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException("NeoForge installer завершился с ошибкой: " + await stderr);
        return "neoforge-" + neo;
    }
}

public static class ProcessLog
{
    public static void Start(Process process, Action<string> log)
    {
        process.StartInfo.UseShellExecute = false; process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true; process.StartInfo.RedirectStandardError = true;
        process.OutputDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => log($"Процесс завершён: {process.ExitCode}." + (process.ExitCode == 0 ? "" : " Проверьте crash-reports и latest.log; OutOfMemoryError означает нехватку памяти, UnsupportedClassVersionError — несовместимую Java."));
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
    }
}
