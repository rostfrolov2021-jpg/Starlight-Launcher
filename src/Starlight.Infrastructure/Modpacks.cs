using System.IO.Compression;
using System.Text.Json;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class ModpackCatalog(ApiClient api, Func<string> curseForgeKey, Func<string?> service)
{
    private const string Modrinth = "https://api.modrinth.com/v2/";
    public async Task<JsonElement> CurseAsync(string path, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(curseForgeKey()))
            return await api.GetAsync("https://api.curseforge.com/v1/" + path, ct, curseForgeKey());
        if (service() is { Length: > 0 } endpoint)
            return await api.GetAsync(new Uri(ApiClient.ServiceUri(endpoint), "api/curseforge/" + path).AbsoluteUri, ct);
        throw new InvalidOperationException("Для CurseForge привяжите сервис с API-ключом или укажите свой ключ в настройках. Modrinth доступен сразу.");
    }
    public async Task<PackPage> SearchAsync(string source, string query, string minecraft, string loader, int offset, string sort, CancellationToken ct)
    {
        if (source == "Modrinth")
        {
            var facets = new List<string[]> { new[] { "project_type:modpack" } };
            if (!string.IsNullOrWhiteSpace(minecraft)) facets.Add(["versions:" + minecraft.Trim()]);
            if (loader != "Все") facets.Add(["categories:" + loader.ToLowerInvariant()]);
            var order = sort == "Обновлённые" ? "updated" : sort == "Новые" ? "newest" : "downloads";
            var json = await api.GetAsync(Modrinth + $"search?limit=25&offset={Math.Max(0, offset)}&index={order}&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}", ct);
            var items = json.GetProperty("hits").EnumerateArray().Select(x => new PackProject(source, x.GetProperty("project_id").GetString()!, x.GetProperty("title").GetString()!,
                x.GetProperty("description").GetString()!, x.GetProperty("author").GetString() ?? "", x.GetProperty("downloads").GetInt64(), "https://modrinth.com/modpack/" + x.GetProperty("slug").GetString())).ToList();
            return new(items, json.GetProperty("total_hits").GetInt32(), offset);
        }
        var type = loader switch { "Forge" => 1, "Fabric" => 4, "NeoForge" => 6, _ => 0 };
        var result = await CurseAsync($"mods/search?gameId=432&classId=4471&pageSize=25&index={Math.Max(0, offset)}&searchFilter={Uri.EscapeDataString(query)}&sortField={(sort == "Обновлённые" ? 3 : sort == "Новые" ? 11 : 6)}&sortOrder=desc"
            + (string.IsNullOrWhiteSpace(minecraft) ? "" : "&gameVersion=" + Uri.EscapeDataString(minecraft.Trim())) + (type == 0 ? "" : "&modLoaderType=" + type), ct);
        return new(result.GetProperty("data").EnumerateArray().Select(x => new PackProject("CurseForge", x.GetProperty("id").ToString(), x.GetProperty("name").GetString()!,
            x.GetProperty("summary").GetString()!, string.Join(", ", x.GetProperty("authors").EnumerateArray().Select(a => a.GetProperty("name").GetString())), (long)x.GetProperty("downloadCount").GetDouble(),
            x.GetProperty("links").GetProperty("websiteUrl").GetString()!)).ToList(), result.GetProperty("pagination").GetProperty("totalCount").GetInt32(), offset);
    }
    public async Task<IReadOnlyList<PackVersion>> VersionsAsync(PackProject project, CancellationToken ct)
    {
        if (project.Source == "Modrinth")
        {
            var json = await api.GetAsync(Modrinth + "project/" + Uri.EscapeDataString(project.Id) + "/version", ct);
            return json.EnumerateArray().Select(v => {
                var file = v.GetProperty("files").EnumerateArray().FirstOrDefault(f => f.GetProperty("filename").GetString()!.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase));
                if (file.ValueKind == JsonValueKind.Undefined) return null;
                return new PackVersion(v.GetProperty("id").GetString()!, v.GetProperty("name").GetString()!, string.Join(", ", v.GetProperty("game_versions").EnumerateArray().Select(x => x.GetString())),
                    string.Join(", ", v.GetProperty("loaders").EnumerateArray().Select(x => x.GetString())), file.GetProperty("url").GetString()!, file.GetProperty("hashes").GetProperty("sha512").GetString(), "SHA512");
            }).OfType<PackVersion>().Take(100).ToList();
        }
        var result = await CurseAsync($"mods/{Uri.EscapeDataString(project.Id)}/files?pageSize=50", ct);
        return result.GetProperty("data").EnumerateArray().Select(v => new PackVersion(v.GetProperty("id").ToString(), v.GetProperty("displayName").GetString()!,
            string.Join(", ", v.GetProperty("gameVersions").EnumerateArray().Select(x => x.GetString())), "CurseForge", v.GetProperty("downloadUrl").GetString() ?? "",
            v.GetProperty("hashes").EnumerateArray().FirstOrDefault(h => h.GetProperty("algo").GetInt32() == 1).GetProperty("value").GetString(), "SHA1")).ToList();
    }
}

public static class SafeArchive
{
    public static string Child(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length == 0 || parts.Any(p => string.IsNullOrEmpty(p) || p is "." or ".." || p.IndexOfAny([':', '\0', '*', '?', '"', '<', '>', '|']) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("Архив содержит небезопасный путь: " + relative);
        var target = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Путь выходит за пределы сборки.");
        return target;
    }
    public static async Task ExtractAsync(ZipArchive archive, string prefix, string root, CancellationToken ct, long maximumBytes = 4L * 1024 * 1024 * 1024)
    {
        long total = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Count > 30_000) throw new InvalidDataException("Слишком много файлов в архиве.");
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Символические ссылки в архиве не поддерживаются.");
            var relative = entry.FullName[prefix.Length..].TrimEnd('/'); if (relative.Length == 0) continue;
            var target = Child(root, relative);
            if (!seen.Add(relative)) throw new InvalidDataException("Повторяющийся путь в архиве.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            total = checked(total + entry.Length); if (total > maximumBytes) throw new InvalidDataException("Распакованный архив превышает допустимый размер.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open(); await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, ct);
        }
    }
    public static void RemoveStaging(string parent, string child)
    {
        var full = Path.GetFullPath(child);
        if (!full.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Некорректный временный каталог.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}

public sealed class ModpackInstaller(PathService paths, Downloads downloads, ModpackCatalog catalog)
{
    public async Task<Instance> DownloadAsync(PackProject project, PackVersion version, string name, int memory, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version.Url)) throw new InvalidOperationException("Автор запретил стороннюю загрузку. Скачайте архив со страницы сборки и используйте «Импорт файла».");
        var archive = Path.Combine(paths.Cache, "packs", Guid.NewGuid().ToString("N") + ".zip");
        await downloads.FileAsync(version.Url, archive, version.Hash, version.Algorithm, progress, ct);
        return await ImportAsync(archive, name, memory, progress, ct, new(project.Source, project.Id, version.Id, project.Name));
    }
    public async Task<Instance> ImportAsync(string archivePath, string name, int memory, IProgress<TransferProgress> progress, CancellationToken ct, PackOrigin? origin = null)
    {
        if (memory is < 512 or > 65536) throw new ArgumentException("Память: 512–65536 МБ.");
        var id = Guid.NewGuid(); var staging = Path.Combine(paths.Cache, "pack-staging", id.ToString("N")); Directory.CreateDirectory(staging);
        var game = Path.Combine(staging, ".minecraft"); Directory.CreateDirectory(game);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var index = archive.GetEntry("modrinth.index.json"); var curseManifest = archive.GetEntry("manifest.json");
            if (index == null && curseManifest == null) throw new InvalidDataException("Ожидался .mrpack или CurseForge ZIP с manifest.json.");
            var entry = index ?? curseManifest!;
            if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Манифест сборки слишком большой.");
            await using var input = entry.Open(); using var doc = await JsonDocument.ParseAsync(input, cancellationToken: ct); var json = doc.RootElement;
            string minecraft; ModLoader loader = ModLoader.Vanilla; string? loaderVersion = null;
            var files = new List<(string Target, string[] Urls, string Hash, string Algorithm)>();
            if (index != null)
            {
                if (json.GetProperty("formatVersion").GetInt32() != 1 || json.GetProperty("game").GetString() != "minecraft") throw new InvalidDataException("Неподдерживаемый формат Modrinth.");
                var dependencies = json.GetProperty("dependencies"); minecraft = dependencies.GetProperty("minecraft").GetString()!;
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    if (dependency.Name == "minecraft") continue;
                    if (loader != ModLoader.Vanilla) throw new InvalidDataException("Сборка содержит несколько несовместимых загрузчиков.");
                    loader = dependency.Name switch { "fabric-loader" => ModLoader.Fabric, "forge" => ModLoader.Forge, "neoforge" => ModLoader.NeoForge, _ => throw new InvalidDataException("Загрузчик этой сборки пока не поддерживается: " + dependency.Name) };
                    loaderVersion = dependency.Value.GetString();
                }
                foreach (var file in json.GetProperty("files").EnumerateArray())
                {
                    if (file.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var client) && client.GetString() == "unsupported") continue;
                    var target = SafeArchive.Child(game, file.GetProperty("path").GetString()!);
                    var hashes = file.GetProperty("hashes"); var strong = hashes.TryGetProperty("sha512", out var hash);
                    files.Add((target, file.GetProperty("downloads").EnumerateArray().Select(x => x.GetString()!).ToArray(), strong ? hash.GetString()! : hashes.GetProperty("sha1").GetString()!, strong ? "SHA512" : "SHA1"));
                }
            }
            else
            {
                if (json.GetProperty("manifestVersion").GetInt32() != 1) throw new InvalidDataException("Неподдерживаемая версия CurseForge manifest.");
                var mc = json.GetProperty("minecraft"); minecraft = mc.GetProperty("version").GetString()!;
                var loaders = mc.GetProperty("modLoaders").EnumerateArray().ToArray();
                if (loaders.Length > 0)
                {
                    var chosen = loaders.FirstOrDefault(x => x.TryGetProperty("primary", out var primary) && primary.GetBoolean());
                    if (chosen.ValueKind == JsonValueKind.Undefined) chosen = loaders[0];
                    var value = chosen.GetProperty("id").GetString()!; var separator = value.IndexOf('-');
                    if (separator < 0) throw new InvalidDataException("Некорректная версия загрузчика в сборке.");
                    loader = value[..separator] switch { "forge" => ModLoader.Forge, "fabric" => ModLoader.Fabric, "neoforge" => ModLoader.NeoForge, _ => throw new InvalidDataException("Неподдерживаемый загрузчик: " + value) }; loaderVersion = value[(separator + 1)..];
                }
                foreach (var file in json.GetProperty("files").EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    var metadata = (await catalog.CurseAsync($"mods/{file.GetProperty("projectID").GetInt32()}/files/{file.GetProperty("fileID").GetInt32()}", ct)).GetProperty("data");
                    var url = metadata.GetProperty("downloadUrl").GetString();
                    if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("CurseForge запретил загрузку: " + metadata.GetProperty("fileName").GetString() + ". Импорт не изменил вашу библиотеку.");
                    var hash = metadata.GetProperty("hashes").EnumerateArray().First(h => h.GetProperty("algo").GetInt32() == 1).GetProperty("value").GetString()!;
                    files.Add((SafeArchive.Child(game, "mods/" + metadata.GetProperty("fileName").GetString()), [url], hash, "SHA1"));
                }
            }
            if (files.Count > 15_000 || files.Select(f => f.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new InvalidDataException("Некорректный список файлов сборки.");
            int completed = 0;
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (file, token) => {
                Exception? error = null;
                foreach (var url in file.Urls)
                {
                    try { await downloads.FileAsync(url, file.Target, file.Hash, file.Algorithm, null, token); error = null; break; }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException) { error = ex; }
                }
                if (file.Urls.Length == 0 || error != null) throw new IOException("Не удалось загрузить " + Path.GetFileName(file.Target), error);
                progress.Report(new($"Файлы сборки: {Interlocked.Increment(ref completed)} / {files.Count}", completed * 95d / Math.Max(1, files.Count)));
            });
            var prefix = index != null ? "overrides" : json.TryGetProperty("overrides", out var overrides) ? overrides.GetString()! : "overrides";
            _ = SafeArchive.Child(staging, prefix);
            await SafeArchive.ExtractAsync(archive, prefix.TrimEnd('/') + "/", game, ct);
            if (index != null) await SafeArchive.ExtractAsync(archive, "client-overrides/", game, ct);
            foreach (var folder in new[] { "mods", "config", "saves", "resourcepacks" }) Directory.CreateDirectory(Path.Combine(game, folder));
            var displayName = string.IsNullOrWhiteSpace(name) ? json.GetProperty("name").GetString()! : name.Trim();
            if (displayName.Length is < 1 or > 80) throw new ArgumentException("Имя сборки: от 1 до 80 символов.");
            var instance = new Instance(id, displayName, minecraft, loader, loaderVersion, memory, "▣");
            await JsonStore.WriteAsync(Path.Combine(staging, "instance.json"), instance, ct);
            if (origin != null) await JsonStore.WriteAsync(Path.Combine(staging, "pack-origin.json"), origin, ct);
            ct.ThrowIfCancellationRequested(); Directory.Move(staging, paths.InstanceRoot(id)); progress.Report(new("Сборка добавлена в библиотеку", 100)); return instance;
        }
        finally { SafeArchive.RemoveStaging(Path.Combine(paths.Cache, "pack-staging"), staging); }
    }
}
