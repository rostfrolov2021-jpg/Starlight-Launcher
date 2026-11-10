using System.Text.Json;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public enum ResourceKind { ResourcePack, Shader }

/// <summary>Modrinth search/install for resource packs and shader packs (mods go through <see cref="ModService"/>).</summary>
public sealed class ResourceCatalog(PathService paths, Downloads downloads)
{
    private const string Api = "https://api.modrinth.com/v2/";
    public static string Folder(ResourceKind kind) => kind == ResourceKind.Shader ? "shaderpacks" : "resourcepacks";
    private static string ProjectType(ResourceKind kind) => kind == ResourceKind.Shader ? "shader" : "resourcepack";
    public string Directory(Instance instance, ResourceKind kind) => Path.Combine(paths.Game(instance.Id), Folder(kind));

    public async Task<IReadOnlyList<ModProject>> SearchAsync(ResourceKind kind, string query, Instance instance, CancellationToken ct)
    {
        var facets = new List<string[]> { new[] { "project_type:" + ProjectType(kind) }, new[] { "versions:" + instance.MinecraftVersion } };
        var json = await downloads.JsonAsync(Api + "search?limit=30&query=" + Uri.EscapeDataString(query) + "&facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets)), ct);
        return json.GetProperty("hits").EnumerateArray().Select(x => new ModProject("Modrinth", x.GetProperty("project_id").GetString()!,
            x.GetProperty("title").GetString()!, x.GetProperty("description").GetString()!,
            x.TryGetProperty("icon_url", out var icon) ? icon.GetString() : null)).ToList();
    }

    public async Task InstallAsync(ResourceKind kind, ModProject project, Instance instance, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var versions = await downloads.JsonAsync(Api + "project/" + Uri.EscapeDataString(project.Id) + "/version?game_versions="
            + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.MinecraftVersion })), ct);
        if (versions.GetArrayLength() == 0) throw new InvalidOperationException($"Нет версии «{project.Name}» для Minecraft {instance.MinecraftVersion}.");
        var files = versions[0].GetProperty("files").EnumerateArray().ToArray();
        var file = files.FirstOrDefault(f => f.GetProperty("primary").GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined) file = files[0];
        var name = file.GetProperty("filename").GetString()!;
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ожидался ZIP-архив.");
        var folder = Directory(instance, kind);
        System.IO.Directory.CreateDirectory(folder);
        await downloads.FileAsync(file.GetProperty("url").GetString()!, PathService.SafeChild(folder, name),
            file.GetProperty("hashes").GetProperty("sha512").GetString(), "SHA512", progress, ct);
    }

    public static IReadOnlyList<LocalMod> Scan(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        return System.IO.Directory.EnumerateFiles(directory, "*.zip").Select(p => new FileInfo(p))
            .Select(f => new LocalMod(f.Name, f.FullName, true, f.Length)).OrderBy(x => x.FileName).ToList();
    }

    public static void Delete(LocalMod item) => File.Delete(item.FullPath);
}
