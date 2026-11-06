using System.Net.Http.Json;
using System.Text.Json;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class ModrinthProvider(Downloads downloads) : IModProvider
{
    public string Name => "Modrinth";
    private const string Api = "https://api.modrinth.com/v2/";
    public async Task<IReadOnlyList<ModProject>> SearchAsync(string query, Instance instance, CancellationToken ct)
    {
        var facets = new List<string[]> { new[] { "project_type:mod" }, new[] { "versions:" + instance.MinecraftVersion } };
        if (instance.Loader != ModLoader.Vanilla) facets.Add(["categories:" + instance.Loader.ToString().ToLowerInvariant()]);
        var json = await downloads.JsonAsync(Api + "search?limit=30&query=" + Uri.EscapeDataString(query) + "&facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets)), ct);
        return json.GetProperty("hits").EnumerateArray().Select(x => new ModProject(Name, x.GetProperty("project_id").GetString()!, x.GetProperty("title").GetString()!, x.GetProperty("description").GetString()!, x.GetProperty("icon_url").GetString())).ToList();
    }
    public async Task<ModFile> ResolveAsync(string projectId, string? versionId, Instance instance, CancellationToken ct)
    {
        JsonElement version;
        if (versionId != null) version = await downloads.JsonAsync(Api + "version/" + Uri.EscapeDataString(versionId), ct);
        else
        {
            var versions = await downloads.JsonAsync(Api + "project/" + Uri.EscapeDataString(projectId) + "/version?game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.MinecraftVersion })) + "&loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.Loader.ToString().ToLowerInvariant() })), ct);
            if (versions.GetArrayLength() == 0) throw new InvalidOperationException($"Нет совместимого файла для {projectId}.");
            version = versions[0];
        }
        if (!version.GetProperty("game_versions").EnumerateArray().Any(x => x.GetString() == instance.MinecraftVersion) || !version.GetProperty("loaders").EnumerateArray().Any(x => x.GetString() == instance.Loader.ToString().ToLowerInvariant()))
            throw new InvalidOperationException("Обязательная зависимость несовместима с выбранной сборкой.");
        var files = version.GetProperty("files").EnumerateArray().ToArray();
        var file = files.FirstOrDefault(f => f.GetProperty("primary").GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined) file = files[0];
        var dependencies = version.GetProperty("dependencies").EnumerateArray().Where(x => x.GetProperty("dependency_type").GetString() == "required")
            .Select(x => new ModDependency(x.GetProperty("project_id").GetString() ?? "", x.GetProperty("version_id").GetString())).ToList();
        return new(version.GetProperty("project_id").GetString()!, version.GetProperty("id").GetString()!, file.GetProperty("filename").GetString()!, file.GetProperty("url").GetString()!, file.GetProperty("hashes").GetProperty("sha512").GetString()!, "SHA512", dependencies);
    }
}

public sealed class CurseForgeProvider(HttpClient http, Func<string> key, Func<string?>? service = null) : IModProvider
{
    public string Name => "CurseForge";
    private static int Loader(Instance i) => i.Loader switch { ModLoader.Forge => 1, ModLoader.Fabric => 4, ModLoader.NeoForge => 6, _ => 0 };
    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        return (await new ModpackCatalog(new ApiClient(http), key, service ?? (() => null)).CurseAsync(path, ct)).GetProperty("data").Clone();
    }
    public async Task<IReadOnlyList<ModProject>> SearchAsync(string query, Instance instance, CancellationToken ct)
    {
        var json = await GetAsync($"mods/search?gameId=432&classId=6&pageSize=30&searchFilter={Uri.EscapeDataString(query)}&gameVersion={Uri.EscapeDataString(instance.MinecraftVersion)}&modLoaderType={Loader(instance)}", ct);
        return json.EnumerateArray().Select(x => new ModProject(Name, x.GetProperty("id").ToString(), x.GetProperty("name").GetString()!, x.GetProperty("summary").GetString()!, x.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object ? logo.GetProperty("thumbnailUrl").GetString() : null)).ToList();
    }
    public async Task<ModFile> ResolveAsync(string projectId, string? versionId, Instance instance, CancellationToken ct)
    {
        JsonElement file;
        if (versionId != null) file = await GetAsync($"mods/{Uri.EscapeDataString(projectId)}/files/{Uri.EscapeDataString(versionId)}", ct);
        else
        {
            var files = await GetAsync($"mods/{Uri.EscapeDataString(projectId)}/files?gameVersion={Uri.EscapeDataString(instance.MinecraftVersion)}&modLoaderType={Loader(instance)}", ct);
            if (files.GetArrayLength() == 0) throw new InvalidOperationException("Нет совместимых файлов CurseForge.");
            file = files[0];
        }
        var url = file.GetProperty("downloadUrl").GetString();
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("Автор запретил сторонние загрузки этого мода. Установите его вручную с CurseForge.");
        var hash = file.GetProperty("hashes").EnumerateArray().First(x => x.GetProperty("algo").GetInt32() == 1).GetProperty("value").GetString()!;
        return new(projectId, file.GetProperty("id").ToString(), file.GetProperty("fileName").GetString()!, url, hash, "SHA1",
            file.GetProperty("dependencies").EnumerateArray().Where(x => x.GetProperty("relationType").GetInt32() == 3).Select(x => new ModDependency(x.GetProperty("modId").ToString())).ToList());
    }
}

public sealed class ModService(PathService paths, Downloads downloads)
{
    public sealed record InstalledMod(string Source, string ProjectId, string VersionId, string FileName);
    private readonly SemaphoreSlim installGate = new(1);
    public async Task InstallAsync(IModProvider provider, ModProject project, Instance instance, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        if (instance.Loader == ModLoader.Vanilla) throw new InvalidOperationException("Для модов создайте сборку Fabric, Forge или NeoForge.");
        await installGate.WaitAsync(ct);
        try
        {
            var resolved = new Dictionary<string, ModFile>(); var visiting = new HashSet<string>();
            async Task Visit(string id, string? version, int depth)
            {
                if (depth > 64 || resolved.Count > 256) throw new InvalidDataException("Слишком большое дерево зависимостей.");
                var key = id + ":" + version;
                if (!visiting.Add(key)) return;
                var file = await provider.ResolveAsync(id, version, instance, ct);
                if (resolved.TryGetValue(file.ProjectId, out var existing))
                {
                    if (existing.VersionId != file.VersionId && version != null) throw new InvalidOperationException("Конфликт версий зависимости: " + file.ProjectId);
                    return;
                }
                resolved.Add(file.ProjectId, file);
                foreach (var dependency in file.Dependencies) await Visit(dependency.ProjectId, dependency.VersionId, depth + 1);
            }
            await Visit(project.Id, null, 0);
            // Stage the entire dependency graph first. A failed transfer never leaves half a mod installed.
            var staged = new List<(string Source, string Target)>();
            foreach (var file in resolved.Values)
            {
                var target = PathService.SafeChild(paths.Mods(instance.Id), file.FileName);
                if (!file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ожидался JAR-файл.");
                var source = Path.Combine(paths.Cache, "mods", file.Hash + ".jar");
                await downloads.FileAsync(file.Url, source, file.Hash, file.HashAlgorithm, progress, ct);
                staged.Add((source, target));
            }
            ct.ThrowIfCancellationRequested();
            var inventoryPath = Path.Combine(paths.InstanceRoot(instance.Id), "installed-mods.json");
            var inventory = await JsonStore.ReadAsync<List<InstalledMod>>(inventoryPath, ct) ?? [];
            var updated = inventory.Where(m => m.Source != provider.Name || !resolved.ContainsKey(m.ProjectId)).ToList();
            var backup = Path.Combine(paths.Cache, "mod-transactions", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(backup);
            var moved = new List<(string Original, string Backup)>(); var created = new List<string>();
            void Preserve(string original)
            {
                if (!File.Exists(original) || moved.Any(m => m.Original == original)) return;
                var copy = Path.Combine(backup, moved.Count.ToString()); File.Move(original, copy); moved.Add((original, copy));
            }
            try
            {
                var index = 0;
                foreach (var file in resolved.Values)
                {
                    var previous = inventory.FirstOrDefault(m => m.Source == provider.Name && m.ProjectId == file.ProjectId);
                    var oldPath = previous == null ? null : PathService.SafeChild(paths.Mods(instance.Id), previous.FileName);
                    var disabled = oldPath != null && File.Exists(oldPath + ".disabled");
                    var target = staged[index].Target + (disabled ? ".disabled" : "");
                    if (updated.Any(m => m.FileName == file.FileName)) throw new InvalidOperationException("Моды используют одинаковое имя файла: " + file.FileName);
                    if (oldPath != null) { Preserve(oldPath); Preserve(oldPath + ".disabled"); }
                    Preserve(target); File.Copy(staged[index].Source, target); created.Add(target);
                    updated.Add(new(provider.Name, file.ProjectId, file.VersionId, file.FileName)); index++;
                }
                await JsonStore.WriteAsync(inventoryPath, updated, CancellationToken.None);
            }
            catch
            {
                foreach (var target in created) File.Delete(target);
                foreach (var move in moved) File.Move(move.Backup, move.Original, true);
                throw;
            }
            // Old JARs remain recoverable in cache/mod-transactions; no worlds or user configs are touched.
        }
        finally { installGate.Release(); }
    }
    public static IReadOnlyList<LocalMod> Scan(string directory)
    {
        Directory.CreateDirectory(directory);
        return Directory.EnumerateFiles(directory).Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            .Select(p => new FileInfo(p)).Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(f => new LocalMod(f.Name, f.FullName, f.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase), f.Length)).OrderBy(x => x.FileName).ToList();
    }
    public static void Toggle(LocalMod mod) => File.Move(mod.FullPath, mod.Enabled ? mod.FullPath + ".disabled" : mod.FullPath[..^9]);
    public static void Delete(LocalMod mod, string cache)
    {
        var trash = Path.Combine(cache, "deleted-mods"); Directory.CreateDirectory(trash);
        File.Move(mod.FullPath, Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + mod.FileName));
    }
}
