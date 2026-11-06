using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class ServerArchive(PathService paths)
{
    public string Root => Path.Combine(paths.Root, "deleted-servers");

    public async Task<string> ArchiveAsync(LocalServer server, CancellationToken ct)
    {
        var source = paths.Server(server.Id);
        CheckChild(paths.Servers, source);
        var saved = await JsonStore.ReadAsync<LocalServer>(Path.Combine(source, "server.json"), ct);
        if (saved?.Id != server.Id) throw new InvalidDataException("Не удалось проверить папку выбранного сервера.");
        Directory.CreateDirectory(Root);
        var destination = Path.Combine(Root, server.Id.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        CheckChild(Root, destination);
        ct.ThrowIfCancellationRequested();
        Directory.Move(source, destination);
        return destination;
    }

    public async Task<LocalServer> RestoreAsync(string archive, CancellationToken ct)
    {
        CheckChild(Root, archive);
        var server = await JsonStore.ReadAsync<LocalServer>(Path.Combine(archive, "server.json"), ct)
            ?? throw new InvalidDataException("В архиве нет данных сервера.");
        var destination = paths.Server(server.Id);
        CheckChild(paths.Servers, destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Этот сервер уже есть в списке. Восстановление не заменяет существующие файлы.");
        ct.ThrowIfCancellationRequested();
        Directory.Move(archive, destination);
        return server;
    }

    private void CheckChild(string parent, string child)
    {
        var full = Path.GetFullPath(child);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(parent), comparison))
            throw new InvalidDataException("Папка находится вне хранилища серверов.");
        for (var directory = new DirectoryInfo(full); directory != null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Удаление и восстановление через ссылку на папку не поддерживается.");
            if (string.Equals(directory.FullName, Path.GetFullPath(paths.Root), comparison)) break;
        }
    }
}
