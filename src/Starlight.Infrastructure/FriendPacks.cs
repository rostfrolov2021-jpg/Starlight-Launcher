using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class PublishedFriendPack(string file, SharedPack description) : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> transfers = new();
    public SharedPack Description { get; } = description;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public void Start() { listener.Start(); loop = AcceptAsync(); }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token); var id = Guid.NewGuid();
                var task = SendAsync(client); transfers[id] = task;
                _ = task.ContinueWith(_ => transfers.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException) { }
    }
    private async Task SendAsync(TcpClient client)
    {
        using (client)
        try { await using var input = File.OpenRead(file); await input.CopyToAsync(client.GetStream(), stop.Token); var end = new byte[1]; await client.GetStream().ReadAsync(end, stop.Token); }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); listener.Stop(); if (loop != null) await loop;
        await Task.WhenAll(transfers.Values); stop.Dispose(); File.Delete(file);
    }
}

public sealed class FriendPacks(PathService paths)
{
    public const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    public static bool Allowed(string relative)
    {
        if (relative.Contains('\\') || relative.Split('/').Any(p => p is "" or "." or ".." || p.Contains(':'))) return false;
        var parts = relative.Split('/'); if (parts.Length < 2) return false;
        var extension = Path.GetExtension(relative).ToLowerInvariant();
        return parts[0] switch {
            "mods" => parts.Length == 2 && extension == ".jar",
            "resourcepacks" => parts.Length == 2 && extension == ".zip",
            "config" or "defaultconfigs" => extension is ".json" or ".json5" or ".toml" or ".cfg" or ".properties" or ".yaml" or ".yml" or ".snbt" or ".txt" or ".ini",
            "kubejs" or "scripts" => extension is ".js" or ".json" or ".zs" or ".png" or ".ogg" or ".mcmeta",
            _ => false };
    }
    public string[] Files(Instance instance)
    {
        var root = paths.Game(instance.Id);
        foreach (var directory in new[] { paths.Instances, paths.InstanceRoot(instance.Id), root })
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Папки-ссылки нельзя передавать друзьям.");
        var files = new List<string>();
        foreach (var name in new[] { "mods", "resourcepacks", "config", "defaultconfigs", "kubejs", "scripts" })
        {
            var folder = Path.Combine(root, name); if (!Directory.Exists(folder)) continue;
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Папки-ссылки нельзя передавать друзьям.");
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/'); if (Allowed(relative)) files.Add(relative);
            }
        }
        if (files.Count > 10000 || files.Sum(f => new FileInfo(SafeArchive.Child(root, f)).Length) > MaximumBytes) throw new InvalidDataException("Сборка для друзей: максимум 10 000 файлов и 4 ГБ.");
        return files.Order(StringComparer.Ordinal).ToArray();
    }
    public async Task<PublishedFriendPack> PublishAsync(Instance instance, string[] approvedFiles, CancellationToken ct)
    {
        if (instance.Loader != ModLoader.Vanilla && string.IsNullOrWhiteSpace(instance.LoaderVersion))
        {
            var saved = await JsonStore.ReadAsync<Instance>(Path.Combine(paths.InstanceRoot(instance.Id), "instance.json"), ct);
            var installed = saved?.InstalledVersion ?? instance.InstalledVersion;
            if (installed != null)
            {
                var folder = PathService.SafeChild(Path.Combine(paths.Game(instance.Id), "versions"), installed);
                var profile = await JsonStore.ReadAsync<JsonElement>(PathService.SafeChild(folder, installed + ".json"), ct);
                if (profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("libraries", out var libraries))
                {
                    var prefix = instance.Loader switch { ModLoader.Fabric => "net.fabricmc:fabric-loader:", ModLoader.Forge => "net.minecraftforge:forge:", _ => "net.neoforged:neoforge:" };
                    var coordinate = libraries.EnumerateArray().Select(l => l.TryGetProperty("name", out var n) ? n.GetString() : null).FirstOrDefault(n => n?.StartsWith(prefix, StringComparison.Ordinal) == true);
                    var version = coordinate?[prefix.Length..].Split(':')[0];
                    if (instance.Loader == ModLoader.Forge && version?.StartsWith(instance.MinecraftVersion + "-", StringComparison.Ordinal) == true) version = version[(instance.MinecraftVersion.Length + 1)..];
                    if (!string.IsNullOrWhiteSpace(version)) instance = instance with { LoaderVersion = version };
                }
            }
        }
        if (instance.Loader != ModLoader.Vanilla && string.IsNullOrWhiteSpace(instance.LoaderVersion)) throw new InvalidOperationException("Для передачи сборки укажите точную версию загрузчика в её настройках.");
        var current = Files(instance);
        if (!current.SequenceEqual(approvedFiles)) throw new InvalidOperationException("Состав сборки изменился. Просмотрите список файлов ещё раз.");
        var file = Path.Combine(paths.Cache, "friend-packs", Guid.NewGuid().ToString("N") + ".zip"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            long size = 0;
            using (var archive = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                var metadata = archive.CreateEntry("starlight.pack.json");
                await using (var output = metadata.Open()) await JsonSerializer.SerializeAsync(output, instance with { Id = Guid.Empty, InstalledVersion = null }, JsonStore.Options, ct);
                foreach (var relative in approvedFiles)
                {
                    ct.ThrowIfCancellationRequested(); var source = SafeArchive.Child(paths.Game(instance.Id), relative);
                    await using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    size = checked(size + input.Length); if (size > MaximumBytes) throw new InvalidDataException("Сборка превышает 4 ГБ.");
                    var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                    await using var output = entry.Open(); await input.CopyToAsync(output, ct);
                }
            }
            await using var zip = File.OpenRead(file); var hash = Convert.ToHexString(await SHA256.HashDataAsync(zip, ct));
            if (zip.Length > MaximumBytes) throw new InvalidDataException("Архив сборки превышает 4 ГБ.");
            var result = new PublishedFriendPack(file, new(instance.Name, instance.MinecraftVersion, instance.Loader, instance.LoaderVersion, zip.Length, hash, current.Length));
            result.Start(); return result;
        }
        catch { File.Delete(file); throw; }
    }
    public static void Validate(SharedPack pack)
    {
        if (string.IsNullOrWhiteSpace(pack.Name) || pack.Name.Length > 80 || string.IsNullOrWhiteSpace(pack.Minecraft) || pack.Minecraft.Length > 64 || !Enum.IsDefined(pack.Loader) || pack.LoaderVersion?.Length > 100 ||
            pack.Bytes is < 1 or > MaximumBytes || pack.Files is < 0 or > 10000 || !System.Text.RegularExpressions.Regex.IsMatch(pack.Sha256 ?? "", "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("Некорректное описание сборки друга.");
    }
    public async Task<Instance> ReceiveAsync(SharedPack pack, int localPort, int memory, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        Validate(pack); var id = Guid.NewGuid(); var parent = Path.Combine(paths.Cache, "friend-staging"); var staging = Path.Combine(parent, id.ToString("N"));
        Directory.CreateDirectory(staging); var archiveFile = Path.Combine(staging, "download.zip");
        try
        {
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, localPort, ct);
            await using (var output = File.Create(archiveFile))
            {
                var buffer = new byte[65536]; long total = 0;
                while (total < pack.Bytes)
                {
                    var read = await tcp.GetStream().ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, pack.Bytes - total)), ct);
                    if (read == 0) throw new IOException("Друг прервал передачу сборки.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct); total += read;
                    progress.Report(new($"Сборка друга: {total / 1048576d:F1} / {pack.Bytes / 1048576d:F1} МБ", total * 90d / pack.Bytes));
                }
            }
            await using (var input = File.OpenRead(archiveFile))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма сборки друга не совпала.");
            using var archive = ZipFile.OpenRead(archiveFile);
            if (archive.Entries.Count != pack.Files + 1 || archive.Entries.Where(e => e.FullName != "starlight.pack.json").Any(e => !Allowed(e.FullName))) throw new InvalidDataException("В сборке есть непредусмотренные файлы.");
            var metadata = archive.GetEntry("starlight.pack.json") ?? throw new InvalidDataException("Нет описания сборки.");
            if (metadata.Length > 8192) throw new InvalidDataException("Описание сборки слишком большое.");
            Instance source;
            await using (var input = metadata.Open()) source = await JsonSerializer.DeserializeAsync<Instance>(input, JsonStore.Options, ct) ?? throw new InvalidDataException();
            if (source.MinecraftVersion != pack.Minecraft || source.Loader != pack.Loader || source.LoaderVersion != pack.LoaderVersion) throw new InvalidDataException("Версия сборки отличается от приглашения.");
            var game = Path.Combine(staging, ".minecraft"); Directory.CreateDirectory(game);
            await SafeArchive.ExtractAsync(archive, "", game, ct, MaximumBytes);
            File.Delete(Path.Combine(game, "starlight.pack.json"));
            var instance = source with { Id = id, Name = ("Друг · " + pack.Name)[..Math.Min(80, 7 + pack.Name.Length)], MemoryMb = Math.Clamp(memory, 512, 65536), InstalledVersion = null };
            await JsonStore.WriteAsync(Path.Combine(staging, "instance.json"), instance, ct);
            await JsonStore.WriteAsync(Path.Combine(staging, "friend-pack.json"), pack, ct);
            archive.Dispose(); File.Delete(archiveFile);
            Directory.Move(staging, paths.InstanceRoot(id)); return instance;
        }
        finally { SafeArchive.RemoveStaging(parent, staging); }
    }
}
