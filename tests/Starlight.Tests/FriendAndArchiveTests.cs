using Starlight.Domain;
using Starlight.Infrastructure;

static class FriendAndArchiveTests
{
    public static async Task<int> RunAsync(string root)
    {
        var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
        async Task Reject<T>(Func<Task> action, string name) where T : Exception { try { await action(); } catch (T) { Check(true, name); return; } throw new Exception("FAIL " + name); }
        Check(FriendLookup.Normalize("  Alice_123  ") == "Alice_123", "friend lookup trims pasted names");
        var id = Guid.NewGuid(); Check(FriendLookup.Normalize(" " + id.ToString("D").ToUpperInvariant() + " ") == id.ToString("N"), "friend lookup accepts formatted and uppercase ID");
        await Reject<ArgumentException>(() => Task.Run(() => FriendLookup.Normalize("player@example.com")), "friend lookup rejects email instead of account name");
        Check(SocialClient.FriendlyError(new Microsoft.AspNetCore.SignalR.HubException("An unexpected error occurred invoking 'RequestFriend' on the server. HubException: Пользователь не найден.")) == "Пользователь не найден.", "friend action strips internal SignalR wrapper");
        Check(SocialDiscovery.Validate(new(1, null)) == null, "unpublished common service does not invent a host");
        Check(SocialDiscovery.Validate(new(1, "https://social.example.test"))?.AbsoluteUri == "https://social.example.test/", "common service configuration normalizes HTTPS address");
        await Reject<InvalidDataException>(() => Task.Run(() => SocialDiscovery.Validate(new(1, "https://localhost"))), "common service rejects loopback address");
        await Reject<ArgumentException>(() => Task.Run(() => SocialDiscovery.Validate(new(1, "http://social.example.test"))), "common service rejects insecure remote address");
        var paths = new PathService(Path.Combine(root, "server-archive"));
        var archive = new ServerArchive(paths);
        var server = new LocalServer(Guid.NewGuid(), "Precious world", "1.21.1", ServerKind.Paper);
        var other = server with { Id = Guid.NewGuid(), Name = "Keep me" };
        await JsonStore.WriteAsync(Path.Combine(paths.Server(server.Id), "server.json"), server);
        await JsonStore.WriteAsync(Path.Combine(paths.Server(other.Id), "server.json"), other);
        var world = Path.Combine(paths.Server(server.Id), "world", "level.dat"); Directory.CreateDirectory(Path.GetDirectoryName(world)!);
        await File.WriteAllBytesAsync(world, [0, 7, 23, 255]);
        var deleted = await archive.ArchiveAsync(server, default);
        Check(!Directory.Exists(paths.Server(server.Id)) && Directory.Exists(paths.Server(other.Id)), "delete removes only selected server from active storage");
        Check((await File.ReadAllBytesAsync(Path.Combine(deleted, "world", "level.dat"))).SequenceEqual(new byte[] { 0, 7, 23, 255 }), "delete preserves world bytes in archive");
        Check(await archive.RestoreAsync(deleted, default) == server && File.Exists(world), "undo restores original server identity and world");
        deleted = await archive.ArchiveAsync(server, default);
        Directory.CreateDirectory(paths.Server(server.Id));
        await Reject<IOException>(() => archive.RestoreAsync(deleted, default), "restore refuses to replace an existing server directory");
        await Reject<InvalidDataException>(() => archive.RestoreAsync(paths.Server(other.Id), default), "restore rejects locations outside deleted-server storage");
        await JsonStore.WriteAsync(Path.Combine(paths.Server(server.Id), "server.json"), other);
        await Reject<InvalidDataException>(() => archive.ArchiveAsync(server, default), "delete checks selected server identity before moving files");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Reject<OperationCanceledException>(() => archive.ArchiveAsync(other, cancelled.Token), "cancelled delete leaves server in place");
        Check(Directory.Exists(paths.Server(other.Id)), "unrelated server survives failed operations");
        return passed;
    }
}
